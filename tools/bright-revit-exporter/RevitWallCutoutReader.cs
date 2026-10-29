// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    // Reads actual native family cut geometry. It does not infer a void from a
    // family/type width, height, bounding box, tessellation or rendered mesh.
    // The caller must still check phase/design option and verify the resulting
    // full-thickness cutter against the actual wall solid before calculating.
    internal static class RevitWallCutoutReader
    {
        internal delegate CurveLoop InstanceCutoutProvider(Document document, Wall wall, FamilyInstance instance, out XYZ cutDirection);
        private const double MetresPerFoot = 0.3048;
        private const string ApiType = "Autodesk.Revit.DB.IFC.ExporterIFCUtils";
        private const string ApiMethod = "GetInstanceCutoutFromWall";
        private const string Signature = "RevitAPIIFC!Autodesk.Revit.DB.IFC.ExporterIFCUtils.GetInstanceCutoutFromWall(Document,Wall,FamilyInstance,out XYZ):CurveLoop";

        internal static bool TryReadRectangle(Document document, Wall wall, FamilyInstance instance, XYZ origin, XYZ axis, XYZ normal,
            double baseZ, double coordinateToleranceFeet, out CivilWallInsert cutout, out string reason)
        {
            cutout = null; reason = String.Empty;
            try
            {
                if (!ValidIdentity(document, wall, instance, out reason)) return false;
                if (!ValidFrame(origin, axis, normal, baseZ, coordinateToleranceFeet))
                    return Refuse("墙局部坐标系或切洞校验容差无效。", out reason);
                InstanceCutoutProvider provider;
                if (!TryResolveProvider(typeof(Document).Assembly, out provider, out reason)) return false;
                return TryReadRectangleWithProvider(document, wall, instance, origin, axis, normal, baseZ, coordinateToleranceFeet, provider, out cutout, out reason);
            }
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                if (IsCancellation(actual) || actual is OutOfMemoryException) throw actual;
                cutout = null;
                return Refuse("实际切洞来源核验失败（" + actual.GetType().Name + "）：" + actual.Message, out reason);
            }
        }

        // A scoped provider seam for project-owned API stand-ins. The production
        // entry point above always resolves its provider from the host itself.
        internal static bool TryReadRectangleWithProvider(Document document, Wall wall, FamilyInstance instance, XYZ origin, XYZ axis, XYZ normal,
            double baseZ, double coordinateToleranceFeet, InstanceCutoutProvider provider, out CivilWallInsert cutout, out string reason)
        {
            cutout = null; reason = String.Empty;
            CurveLoop loop = null;
            try
            {
                if (!ValidIdentity(document, wall, instance, out reason)) return false;
                if (!ValidFrame(origin, axis, normal, baseZ, coordinateToleranceFeet))
                    return Refuse("墙局部坐标系或切洞校验容差无效。", out reason);
                if (provider == null) return Refuse("官方实际切洞读取方法不可用；未使用门窗名义尺寸。", out reason);
                XYZ cutDirection;
                loop = provider(document, wall, instance, out cutDirection);
                if (loop == null) return Refuse("官方 API 未返回实际切洞曲线环；未使用门窗名义尺寸。", out reason);
                if (!Parallel(cutDirection, normal)) return Refuse("实际切洞方向不是墙法向，不按整厚矩形洞口计算。", out reason);
                var starts = new List<Point>(); var ends = new List<Point>();
                foreach (Curve curve in loop)
                {
                    Line line = curve as Line;
                    if (line == null || !line.IsBound) return Refuse("实际切洞边界不是四条有界直线；未用包围盒替代曲线。", out reason);
                    if (starts.Count == 4) return Refuse("实际切洞曲线环超过四条边，本版仅支持经确认的矩形。", out reason);
                    XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
                    Point a, b;
                    if (!Project(start, origin, axis, normal, baseZ, out a) || !Project(end, origin, axis, normal, baseZ, out b))
                        return Refuse("实际切洞边界含非有限坐标。", out reason);
                    starts.Add(a); ends.Add(b);
                }
                if (starts.Count != 4) return Refuse("实际切洞曲线环不是完整四边形。", out reason);
                double tolerance = coordinateToleranceFeet;
                double planeN = starts[0].N;
                int horizontal = 0, vertical = 0;
                for (int index = 0; index < 4; index++)
                {
                    Point a = starts[index], b = ends[index];
                    if (!Near(b, starts[(index + 1) % 4], tolerance)) return Refuse("实际切洞曲线环断开或未按连接顺序闭合。", out reason);
                    if (Math.Abs(a.N - planeN) > tolerance || Math.Abs(b.N - planeN) > tolerance)
                        return Refuse("实际切洞边界不在平行墙轴与竖向的同一平面。", out reason);
                    double dx = Math.Abs(b.X - a.X), dz = Math.Abs(b.Z - a.Z);
                    if (!Finite(dx) || !Finite(dz)) return Refuse("实际切洞边长溢出。", out reason);
                    if (dx > tolerance && dz <= tolerance) horizontal++;
                    else if (dz > tolerance && dx <= tolerance) vertical++;
                    else return Refuse("实际切洞含斜边或退化边，不是墙轴向矩形。", out reason);
                }
                if (horizontal != 2 || vertical != 2) return Refuse("实际切洞未形成两条横边与两条竖边。", out reason);
                // Extrema are used only after every exact native edge has been
                // checked; four distinct corners below prove a rectangle rather
                // than treating an arbitrary loop's bounds as an opening.
                double x0 = starts.Min(point => point.X), x1 = starts.Max(point => point.X);
                double z0 = starts.Min(point => point.Z), z1 = starts.Max(point => point.Z);
                if (!Finite(x1 - x0) || !Finite(z1 - z0) || x1 - x0 <= tolerance || z1 - z0 <= tolerance)
                    return Refuse("实际矩形切洞宽高无效或退化。", out reason);
                var corners = new HashSet<int>();
                for (int index = 0; index < 4; index++)
                {
                    int a = Corner(starts[index], x0, x1, z0, z1, tolerance);
                    int b = Corner(ends[index], x0, x1, z0, z1, tolerance);
                    if (a < 0 || b < 0 || !corners.Add(a) || a == b)
                        return Refuse("实际切洞没有四个不同的矩形角点，可能包含重复边或折返。", out reason);
                }
                if (!ValidIdentity(document, wall, instance, out reason)) return false;
                string category = NumericId(instance.Category.Id) == (long)BuiltInCategory.OST_Doors ? "door" : "window";
                cutout = new CivilWallInsert {
                    ElementId = Id(instance.Id), UniqueId = instance.UniqueId, Name = instance.Name,
                    HostUniqueId = wall.UniqueId, Kind = "rect-opening", CutoutMethod = "family-rect-cutout", Status = "recognized",
                    RectX0 = x0 * MetresPerFoot, RectX1 = x1 * MetresPerFoot,
                    RectZ0 = z0 * MetresPerFoot, RectZ1 = z1 * MetresPerFoot,
                    Source = Signature + "; actual " + category + " cutout; four native Line edges; closed coplanar wall-axis rectangle; cut direction parallel to wall normal; host ElementId="
                        + Id(wall.Id) + "; family ElementId=" + Id(instance.Id) + "; projected to wall axis and base Z; nominal dimensions not read; whole-wall solid proof still required"
                };
                return true;
            }
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                if (IsCancellation(actual) || actual is OutOfMemoryException) throw actual;
                cutout = null;
                return Refuse("实际切洞读取失败（" + actual.GetType().Name + "）；未使用门窗名义尺寸：" + actual.Message, out reason);
            }
            finally
            {
                // The returned container is owned by this read. Curve wrappers,
                // endpoint XYZ values and source document elements are borrowed;
                // do not dispose shared native source geometry individually.
                if (loop != null) loop.Dispose();
            }
        }

        private static bool ValidIdentity(Document document, Wall wall, FamilyInstance instance, out string reason)
        {
            reason = String.Empty;
            if (document == null || !document.IsValidObject || document.IsFamilyDocument || wall == null || instance == null || !wall.IsValidObject || !instance.IsValidObject)
                return Refuse("实际切洞必须来自有效的项目文档、墙和门窗实例。", out reason);
            if (wall.Document == null || instance.Document == null || !wall.Document.Equals(document) || !instance.Document.Equals(document))
                return Refuse("门窗实例与墙不属于同一个当前项目文档。", out reason);
            if (wall.Id == null || instance.Id == null || NumericId(wall.Id) <= 0 || NumericId(instance.Id) <= 0 ||
                String.IsNullOrWhiteSpace(wall.UniqueId) || String.IsNullOrWhiteSpace(instance.UniqueId))
                return Refuse("门窗或墙的来源身份缺失。", out reason);
            Element liveWall = document.GetElement(wall.Id), liveInstance = document.GetElement(instance.Id), host = instance.Host;
            if (!(liveWall is Wall) || !(liveInstance is FamilyInstance) || !liveWall.IsValidObject || !liveInstance.IsValidObject ||
                liveWall.UniqueId != wall.UniqueId || liveInstance.UniqueId != instance.UniqueId || host == null || !host.IsValidObject ||
                host.Document == null || !host.Document.Equals(document) || !host.Id.Equals(wall.Id) || host.UniqueId != wall.UniqueId)
                return Refuse("门窗宿主或当前文档中的原生对象身份不匹配。", out reason);
            if (instance.Category == null || instance.Category.Id == null ||
                (NumericId(instance.Category.Id) != (long)BuiltInCategory.OST_Doors && NumericId(instance.Category.Id) != (long)BuiltInCategory.OST_Windows))
                return Refuse("仅核验原生门或窗类别的实际切洞，不推断其他族 void。", out reason);
            return true;
        }

        // Only the authoritative host API directory is accepted. No plugin
        // directory, search-path probing, package download or copied IFC DLL.
        private static bool TryResolveProvider(Assembly hostApi, out InstanceCutoutProvider provider, out string reason)
        {
            provider = null; reason = String.Empty;
            try
            {
                if (hostApi == null || hostApi.IsDynamic || hostApi.GetName().Name != "RevitAPI" || String.IsNullOrEmpty(hostApi.Location))
                    return Refuse("当前进程没有可核验位置的 RevitAPI 宿主；官方切洞 API 不可用。", out reason);
                string directory = Path.GetDirectoryName(Path.GetFullPath(hostApi.Location));
                Assembly ifc;
                if (!TryLoadOfficialAssembly(directory, out ifc, out reason)) return false;
                Type type = ifc.GetType(ApiType, false, false);
                MethodInfo method;
                if (type == null || !type.IsPublic || type.Assembly != ifc || !TryExactMethod(type, out method))
                    return Refuse("宿主官方 IFC 程序集没有兼容的实际切洞方法精确签名。", out reason);
                provider = delegate(Document document, Wall wall, FamilyInstance instance, out XYZ cutDirection) {
                    object[] arguments = { document, wall, instance, null };
                    CurveLoop result = (CurveLoop)method.Invoke(null, arguments);
                    cutDirection = arguments[3] as XYZ;
                    return result;
                };
                return true;
            }
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                if (IsCancellation(actual) || actual is OutOfMemoryException) throw actual;
                return Refuse("宿主官方 IFC 切洞 API 不可用（" + actual.GetType().Name + "）：" + actual.Message, out reason);
            }
        }

        // Kept internal to exercise the missing-library/path checks with project
        // fixtures; production supplies only typeof(Document).Assembly's folder.
        internal static bool TryLoadOfficialAssembly(string hostDirectory, out Assembly assembly, out string reason)
        {
            assembly = null; reason = String.Empty;
            try { return LoadOfficialAssembly(hostDirectory, out assembly, out reason); }
            catch (Exception error)
            {
                Exception actual = Unwrap(error);
                if (IsCancellation(actual) || actual is OutOfMemoryException) throw actual;
                assembly = null;
                return Refuse("宿主官方 IFC 程序集无法核验或加载（" + actual.GetType().Name + "）：" + actual.Message, out reason);
            }
        }

        private static bool LoadOfficialAssembly(string hostDirectory, out Assembly assembly, out string reason)
        {
            assembly = null; reason = String.Empty;
            if (String.IsNullOrWhiteSpace(hostDirectory)) return Refuse("缺少 RevitAPI 宿主目录。", out reason);
            string path = Path.GetFullPath(Path.Combine(hostDirectory, "RevitAPIIFC.dll"));
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name != "RevitAPIIFC") continue;
                if (loaded.IsDynamic || !SamePath(loaded.Location, path))
                    return Refuse("进程已从其他目录加载同名 IFC 程序集，未调用该程序集。", out reason);
                assembly = loaded;
            }
            if (assembly != null) return true;
            if (!File.Exists(path)) return Refuse("Revit 宿主目录未安装 RevitAPIIFC.dll，实际门窗切洞暂不可用。", out reason);
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            if (name.Name != "RevitAPIIFC") return Refuse("宿主目录 IFC 文件的程序集名称不匹配。", out reason);
            assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Name != "RevitAPIIFC" || assembly.IsDynamic || !SamePath(assembly.Location, path))
            { assembly = null; return Refuse("实际加载的 IFC 程序集不属于当前 Revit 宿主目录。", out reason); }
            return true;
        }

        internal static bool TryExactMethod(Type type, out MethodInfo method)
        {
            method = null;
            if (type == null) return false;
            Type[] parameters = { typeof(Document), typeof(Wall), typeof(FamilyInstance), typeof(XYZ).MakeByRefType() };
            foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (candidate.Name != ApiMethod || candidate.ReturnType != typeof(CurveLoop) || candidate.ContainsGenericParameters) continue;
                ParameterInfo[] args = candidate.GetParameters();
                if (args.Length != 4 || !args[3].IsOut || args.Any(argument => argument.IsOptional) || args.Take(3).Any(argument => argument.IsOut)) continue;
                bool exact = true;
                for (int index = 0; index < parameters.Length; index++) if (args[index].ParameterType != parameters[index]) exact = false;
                if (!exact) continue;
                if (method != null) { method = null; return false; }
                method = candidate;
            }
            return method != null;
        }

        private sealed class Point { internal double X, N, Z; }
        private static bool Project(XYZ point, XYZ origin, XYZ axis, XYZ normal, double baseZ, out Point result)
        {
            result = null;
            if (!Finite(point)) return false;
            XYZ relative = point - origin;
            double x = relative.DotProduct(axis), n = relative.DotProduct(normal), z = point.Z - baseZ;
            if (!Finite(x) || !Finite(n) || !Finite(z)) return false;
            result = new Point { X = x, N = n, Z = z }; return true;
        }
        private static bool ValidFrame(XYZ origin, XYZ axis, XYZ normal, double baseZ, double tolerance)
        {
            return Finite(origin) && Finite(axis) && Finite(normal) && Finite(baseZ) && Finite(tolerance) && tolerance > 0 &&
                Math.Abs(axis.Z) <= 1e-9 && Math.Abs(normal.Z) <= 1e-9 && Math.Abs(axis.DotProduct(axis) - 1) <= 1e-9 &&
                Math.Abs(normal.DotProduct(normal) - 1) <= 1e-9 && Math.Abs(axis.DotProduct(normal)) <= 1e-9;
        }
        private static bool Parallel(XYZ direction, XYZ normal)
        {
            if (!Finite(direction)) return false;
            double length = Math.Sqrt(direction.DotProduct(direction));
            if (!Finite(length) || length <= 0) return false;
            double projection = direction.DotProduct(normal) / length;
            if (!Finite(projection)) return false;
            double sign = projection >= 0 ? 1 : -1;
            return Math.Abs(direction.X / length - sign * normal.X) <= 1e-9 &&
                Math.Abs(direction.Y / length - sign * normal.Y) <= 1e-9 && Math.Abs(direction.Z / length - sign * normal.Z) <= 1e-9;
        }
        private static bool Near(Point a, Point b, double tolerance)
        {
            double dx = Math.Abs(a.X - b.X), dn = Math.Abs(a.N - b.N), dz = Math.Abs(a.Z - b.Z);
            if (!Finite(dx) || !Finite(dn) || !Finite(dz) || dx > tolerance || dn > tolerance || dz > tolerance) return false;
            return Math.Sqrt(dx * dx + dn * dn + dz * dz) <= tolerance;
        }
        private static int Corner(Point point, double x0, double x1, double z0, double z1, double tolerance)
        {
            int x = Math.Abs(point.X - x0) <= tolerance ? 0 : Math.Abs(point.X - x1) <= tolerance ? 1 : -1;
            int z = Math.Abs(point.Z - z0) <= tolerance ? 0 : Math.Abs(point.Z - z1) <= tolerance ? 1 : -1;
            return x < 0 || z < 0 ? -1 : x + z * 2;
        }
        private static bool SamePath(string left, string right) { return !String.IsNullOrEmpty(left) && String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        private static bool Refuse(string message, out string reason) { reason = message; return false; }
        private static bool Finite(XYZ point) { return point != null && Finite(point.X) && Finite(point.Y) && Finite(point.Z); }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static Exception Unwrap(Exception error) { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; return error; }
        private static bool IsCancellation(Exception error) { return error is OperationCanceledException || error.GetType().FullName == "Autodesk.Revit.Exceptions.OperationCanceledException"; }
        private static long NumericId(ElementId id)
        {
#if REVIT_2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
        private static string Id(ElementId id) { return NumericId(id).ToString(CultureInfo.InvariantCulture); }
    }
}
