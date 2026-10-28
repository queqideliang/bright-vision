using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using BrightVision.Export;

namespace BrightVision.Revit
{
    // Independent extraction through Autodesk APIs. Auxiliary metadata records
    // declare their scope; they are not invented geometry or inferred containment.
    internal sealed class ExportAttachments
    {
        private const double Metres = 0.3048;
        private readonly PackageWriter writer;
        private readonly ExportOptions options;
        private readonly Func<bool> cancelled;
        private readonly Dictionary<Document, DocumentIndex> documents = new Dictionary<Document, DocumentIndex>();
        private readonly HashSet<string> placements = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> systems = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> spaces = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> disciplines = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly List<object> grids = new List<object>();
        private readonly List<object> drawings = new List<object>();
        private readonly Dictionary<string, string> textureFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warnings = new HashSet<string>(StringComparer.Ordinal);
        private int cadCount;
        private int spaceUnassigned;
        private int drawingFileCount;
        private static readonly object propertyCacheLock = new object();
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> propertyCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        internal ExportAttachments(PackageWriter writer, ExportOptions options, Func<bool> cancelled)
        { this.writer = writer; this.options = options; this.cancelled = cancelled; }

        internal void CaptureDocument(Document document, string scope, string placement, Transform transform)
        {
            CheckCancel();
            string placementKey = scope + "|" + placement;
            if (!placements.Add(placementKey)) return;
            DocumentIndex index;
            if (!documents.TryGetValue(document, out index))
            {
                index = new DocumentIndex();
                documents.Add(document, index);
                if (options.IncludeSystemTree) IndexSystems(document, index);
                if (options.IncludeSpaceTree)
                {
                    foreach (SpatialElement spatial in new FilteredElementCollector(document).OfClass(typeof(SpatialElement)))
                    {
                        CheckCancel();
                        index.Spaces.Add(spatial.UniqueId, spatial);
                    }
                }
            }
            if (options.IncludeSystemTree)
                foreach (MEPSystem system in index.Systems.Values)
                    GetGroup(systems, GroupKey(scope, placement, system.UniqueId), system.Name, system.UniqueId, scope, placement, "Revit MEPSystem members");
            if (options.IncludeSpaceTree)
                foreach (SpatialElement spatial in index.Spaces.Values)
                {
                    Dictionary<string, object> group = GetGroup(spaces, GroupKey(scope, placement, spatial.UniqueId), spatial.Name, spatial.UniqueId, scope, placement, "Revit Room/Space membership; no geometric containment inference");
                    group["number"] = ReadString(spatial, "Number");
                    group["spatialType"] = spatial.GetType().Name;
                }
            if (options.IncludeGrids)
                foreach (Grid grid in new FilteredElementCollector(document).OfClass(typeof(Grid)))
                {
                    CheckCancel();
                    Curve curve = grid.Curve;
                    if (curve == null) continue;
                    List<object> points = new List<object>();
                    foreach (XYZ point in curve.Tessellate())
                    {
                        XYZ world = transform.OfPoint(point);
                        points.Add(new double[] { world.X * Metres, world.Y * Metres, world.Z * Metres });
                    }
                    grids.Add(PackageWriter.Fields("id", GroupKey(scope, placement, grid.UniqueId), "name", grid.Name,
                        "sourceUniqueId", grid.UniqueId, "documentScope", scope, "linkInstanceScope", placement,
                        "curveType", curve.GetType().Name, "points", points, "unit", "m"));
                }
        }

        private void IndexSystems(Document document, DocumentIndex index)
        {
            foreach (MEPSystem system in new FilteredElementCollector(document).OfClass(typeof(MEPSystem)))
            {
                CheckCancel();
                index.Systems.Add(system.UniqueId, system);
                // Elements is terminals on some systems. Include documented
                // duct/piping networks and base equipment when exposed.
                IndexMembers(index, system, system.Elements);
                IndexMembers(index, system, ReadProperty(system, "DuctNetwork") as IEnumerable);
                IndexMembers(index, system, ReadProperty(system, "PipingNetwork") as IEnumerable);
                Element equipment = ReadProperty(system, "BaseEquipment") as Element;
                if (equipment != null) AddMembership(index, equipment.UniqueId, system.UniqueId);
            }
        }

        private static void IndexMembers(DocumentIndex index, MEPSystem system, IEnumerable members)
        {
            if (members == null) return;
            foreach (object value in members)
            {
                Element element = value as Element;
                if (element != null) AddMembership(index, element.UniqueId, system.UniqueId);
            }
        }

        private static void AddMembership(DocumentIndex index, string element, string system)
        {
            List<string> list;
            if (!index.Members.TryGetValue(element, out list)) { list = new List<string>(); index.Members.Add(element, list); }
            if (!list.Contains(system)) list.Add(system);
        }

        internal void RecordElement(Element element, string component, string scope, string placement)
        {
            if (element is ImportInstance) cadCount++;
            DocumentIndex index = documents[element.Document];
            if (options.IncludeSystemTree)
            {
                List<string> memberOf;
                if (index.Members.TryGetValue(element.UniqueId, out memberOf))
                    foreach (string systemId in memberOf)
                        AddReference(systems[GroupKey(scope, placement, systemId)], component);
            }
            if (options.IncludeSpaceTree)
            {
                SpatialElement spatial = element as SpatialElement;
                FamilyInstance family = element as FamilyInstance;
                if (spatial == null && family != null)
                {
                    // These properties use Revit's last-phase membership. Do not
                    // claim a phase-specific or geometric point-in-room result.
                    spatial = ReadProperty(family, "Space") as SpatialElement;
                    if (spatial == null) spatial = ReadProperty(family, "Room") as SpatialElement;
                }
                if (spatial != null)
                {
                    Dictionary<string, object> group = GetGroup(spaces, GroupKey(scope, placement, spatial.UniqueId), spatial.Name, spatial.UniqueId, scope, placement,
                        "Revit Room/Space property (last phase); no geometric containment inference");
                    group["number"] = ReadString(spatial, "Number");
                    AddReference(group, component);
                }
                else spaceUnassigned++;
            }
            if (options.IncludeDisciplineTree)
            {
                string discipline = Discipline(element);
                Dictionary<string, object> group = GetGroup(disciplines, discipline, discipline, "", "", "", "BV category mapping v1; unrecognized categories remain Other");
                AddReference(group, component);
            }
        }

        private static string Discipline(Element element)
        {
            if (element.Category == null) return "其他 / Other";
#if REVIT_2024_OR_GREATER
            long id = element.Category.Id.Value;
#else
            int id = element.Category.Id.IntegerValue;
#endif
            string category = Enum.GetName(typeof(BuiltInCategory), id) ?? "";
            if (Has(category, "Electrical", "Lighting", "CableTray", "Conduit", "Communication", "DataDevices", "FireAlarm", "SecurityDevices", "Telephone")) return "电气 / Electrical";
            if (Has(category, "Pipe", "Piping", "Plumbing", "Sprinkler")) return "给排水 / Plumbing";
            if (Has(category, "Duct", "Mechanical", "AirTerminal", "HVAC")) return "暖通 / Mechanical";
            if (Has(category, "Structural", "Rebar", "FabricReinforcement", "Truss", "Brace")) return "结构 / Structural";
            if (Has(category, "Walls", "Floors", "Roofs", "Ceilings", "Doors", "Windows", "Stairs", "Ramps", "Railings", "Curtain", "Rooms", "Furniture", "Casework", "Columns")) return "建筑 / Architectural";
            return "其他 / Other";
        }

        private static bool Has(string value, params string[] parts)
        { foreach (string part in parts) if (value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }

        private static string GroupKey(string scope, string placement, string source)
        { return "a-" + PackageWriter.Hash(scope + "|" + placement + "|" + source).Substring(0, 32); }

        private static Dictionary<string, object> GetGroup(Dictionary<string, Dictionary<string, object>> groups, string id, string name, string source, string scope, string placement, string basis)
        {
            Dictionary<string, object> group;
            if (!groups.TryGetValue(id, out group))
            {
                group = PackageWriter.Fields("id", id, "name", name, "sourceUniqueId", source, "documentScope", scope,
                    "linkInstanceScope", placement, "basis", basis, "elements", new List<object>());
                groups.Add(id, group);
            }
            return group;
        }

        private static void AddReference(Dictionary<string, object> group, string component)
        { ((List<object>)group["elements"]).Add(PackageWriter.Fields("id", component)); }

        internal void ApplyTexture(Document document, MaterialNode node, Dictionary<string, object> material)
        {
            CheckCancel();
            DocumentIndex index = documents[document];
            string cacheKey = null;
            if (!node.HasOverriddenAppearance && node.MaterialId != null && !node.MaterialId.Equals(ElementId.InvalidElementId))
            {
                cacheKey = node.MaterialId.ToString();
                string existing;
                if (index.TextureBindings.TryGetValue(cacheKey, out existing))
                {
                    if (existing != null) material["map"] = existing;
                    return;
                }
                index.TextureBindings.Add(cacheKey, null);
            }
            object appearance = null;
            bool writingAsset = false;
            try
            {
                // Reflection avoids the namespace move of Visual.Asset between
                // supported Revit API generations. Only fixed Autodesk members
                // are read; no external code/assembly is loaded.
                MethodInfo getter = node.GetType().GetMethod(node.HasOverriddenAppearance ? "GetAppearanceOverride" : "GetAppearance", Type.EmptyTypes);
                if (getter != null) appearance = getter.Invoke(node, null);
                if (appearance == null) return;
                object bitmap = FindDiffuseBitmap(appearance);
                if (bitmap == null) return;
                string rawPath = ReadString(bitmap, "Value");
                if (String.IsNullOrWhiteSpace(rawPath)) return;
                string source = ResolveTexture(document, rawPath);
                if (source == null) { Warn("部分材质颜色贴图未找到本地 PNG/JPEG/WebP 文件；保留材质颜色"); return; }
                string relative;
                if (!textureFiles.TryGetValue(source, out relative))
                {
                    relative = "texture/" + PackageWriter.Hash(source).Substring(0, 32) + Path.GetExtension(source).ToLowerInvariant();
                    writingAsset = true;
                    writer.AddFile(relative, source);
                    writingAsset = false;
                    textureFiles.Add(source, relative);
                }
                material["map"] = relative;
                if (cacheKey != null) index.TextureBindings[cacheKey] = relative;
            }
            catch (Exception error)
            {
                if (writingAsset || error is OperationCanceledException || error is IOException || error is UnauthorizedAccessException || error is OutOfMemoryException) throw;
                // Missing/unsupported appearance assets are an explicit partial
                // result. They never turn a requested bitmap into a fake file.
                Warn("部分材质外观无法读取；对应构件保留颜色，贴图数量以实际写入为准");
            }
            finally
            {
                IDisposable disposable = appearance as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        private static object FindDiffuseBitmap(object asset)
        {
            int size = ReadInt(asset, "Size");
            for (int i = 0; i < size && i < 512; i++)
            {
                object property = IndexedProperty(asset, i);
                string name = ReadString(property, "Name").ToLowerInvariant();
                // Never substitute a bump/normal/roughness map as a color map.
                if (!(name.Contains("diffuse") || name.Contains("albedo") || name.Contains("base_color") || name.EndsWith("_color", StringComparison.Ordinal))) continue;
                int visited = 0;
                object bitmap = FindBitmap(property, 0, ref visited);
                if (bitmap != null) return bitmap;
            }
            return null;
        }

        private static object FindBitmap(object property, int depth, ref int visited)
        {
            if (property == null || depth > 8 || ++visited > 512) return null;
            string name = ReadString(property, "Name");
            if (String.Equals(name, "unifiedbitmap_Bitmap", StringComparison.OrdinalIgnoreCase)) return property;
            int size = ReadInt(property, "Size");
            for (int i = 0; i < size && i < 512; i++)
            {
                object found = FindBitmap(IndexedProperty(property, i), depth + 1, ref visited);
                if (found != null) return found;
            }
            int connections = ReadInt(property, "NumberOfConnectedProperties");
            MethodInfo connected = property.GetType().GetMethod("GetConnectedProperty", new Type[] { typeof(int) });
            if (connected != null)
                for (int i = 0; i < connections && i < 64; i++)
                {
                    object found = FindBitmap(connected.Invoke(property, new object[] { i }), depth + 1, ref visited);
                    if (found != null) return found;
                }
            return null;
        }

        private static object IndexedProperty(object value, int index)
        {
            if (value == null) return null;
            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int)) return property.GetValue(value, new object[] { index });
            }
            return null;
        }

        private static string ResolveTexture(Document document, string raw)
        {
            List<string> roots = new List<string>();
            string modelPath = document.PathName;
            if (!String.IsNullOrEmpty(modelPath) && Path.IsPathRooted(modelPath)) roots.Add(Path.GetDirectoryName(modelPath));
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            string commonX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
            foreach (string basePath in new string[] { common, commonX86 })
                if (!String.IsNullOrEmpty(basePath))
                {
                    roots.Add(Path.Combine(basePath, "Autodesk Shared", "Materials", "Textures"));
                    roots.Add(Path.Combine(basePath, "Autodesk Shared", "Materials", "Textures", "1", "Mats"));
                    roots.Add(Path.Combine(basePath, "Autodesk Shared", "Materials", "Textures", "2", "Mats"));
                    roots.Add(Path.Combine(basePath, "Autodesk Shared", "Materials", "Textures", "3", "Mats"));
                }
            foreach (string part in raw.Split('|'))
            {
                string path = part.Trim().Trim('"');
                if (path.Length == 0 || path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf("://", StringComparison.Ordinal) >= 0) continue;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".webp") continue;
                if (Path.IsPathRooted(path)) { if (File.Exists(path)) return Path.GetFullPath(path); continue; }
                foreach (string directory in roots)
                {
                    string candidate = Path.GetFullPath(Path.Combine(directory, path));
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        internal void Write(Document root)
        {
            CheckCancel();
            if (options.IncludeSystemTree) writer.AddJsonFile("metadata/systems.json", PackageWriter.Fields("schema", "BV.Systems/1", "scope", "visited-documents; references limited to exported components", "systems", Values(systems)));
            if (options.IncludeSpaceTree) writer.AddJsonFile("metadata/spaces.json", PackageWriter.Fields("schema", "BV.Spaces/1", "scope", "visited-documents; family membership uses Revit last phase", "unassignedComponentRecords", spaceUnassigned, "spaces", Values(spaces)));
            if (options.IncludeDisciplineTree) writer.AddJsonFile("metadata/disciplines.json", PackageWriter.Fields("schema", "BV.Disciplines/1", "mapping", "BV category mapping v1", "disciplines", Values(disciplines)));
            if (options.IncludeGrids) writer.AddJsonFile("metadata/grids.json", PackageWriter.Fields("schema", "BV.Grids/1", "scope", "all grids in visited documents; tessellated model curves", "unit", "m", "grids", grids));
            if (options.Include2DViews) WriteDrawings(root);
            if (options.IncludeTextures) Warn("贴图范围为可解析的本地颜色位图；不包含程序化材质或完整渲染外观");
            if (options.IncludeCadLinks) Warn("CAD 范围为所选三维视图中的可见网格；不包含原始 CAD 文件或完整二维曲线");
        }

        private void WriteDrawings(Document document)
        {
            if (options.Views2D == null || options.Views2D.Count == 0) throw new InvalidDataException("已勾选二维导出，但未选择二维视图。");
            List<ElementId> ids = new List<ElementId>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (ElementId id in options.Views2D)
            {
                View view = document.GetElement(id) as View;
                if (view == null || view is View3D || view.IsTemplate || !view.CanBePrinted) throw new InvalidDataException("选中的二维视图不能导出 DWG。");
                if (!unique.Add(view.UniqueId)) continue;
                ids.Add(id);
                drawings.Add(PackageWriter.Fields("sourceUniqueId", view.UniqueId, "name", view.Name, "viewType", view.ViewType.ToString(), "format", "DWG"));
            }
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)), ".bv-drawings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string verifiedRoot = Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try
            {
                CheckCancel();
                using (DWGExportOptions dwg = new DWGExportOptions())
                {
                    dwg.MergedViews = true;
                    if (!document.Export(temporary, "BV", ids, dwg)) throw new InvalidDataException("Revit 未完成选定二维视图的 DWG 导出。");
                }
                CheckCancel();
                foreach (string path in Directory.GetFiles(temporary, "*", SearchOption.AllDirectories))
                {
                    string absolute = Path.GetFullPath(path);
                    if (!absolute.StartsWith(verifiedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected DWG output path.");
                    if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unexpected link in DWG output.");
                    string relative = absolute.Substring(verifiedRoot.Length).Replace('\\', '/');
                    writer.AddFile("drawings/" + relative, absolute);
                    drawingFileCount++;
                }
                if (drawingFileCount == 0) throw new InvalidDataException("Revit 未生成二维 DWG 文件。");
                writer.AddJsonFile("metadata/drawings.json", PackageWriter.Fields("schema", "BV.Drawings/1", "views", drawings, "files", drawingFileCount, "viewerSupport", "attachments; open extracted DWG in a compatible application"));
            }
            finally
            {
                // This exact random directory was created above for this export.
                // Refuse cleanup if its identity/location has changed.
                string current = Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (String.Equals(current, verifiedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0)
                {
                    try { Directory.Delete(temporary, true); }
                    catch (IOException) { Warn("二维临时目录被占用，未自动清理：" + temporary); }
                    catch (UnauthorizedAccessException) { Warn("二维临时目录无法清理：" + temporary); }
                }
            }
        }

        private static List<object> Values(Dictionary<string, Dictionary<string, object>> map)
        { List<object> result = new List<object>(); foreach (Dictionary<string, object> value in map.Values) result.Add(value); return result; }

        internal Dictionary<string, object> OptionManifest()
        {
            return PackageWriter.Fields("geometry", true, "properties", true, "textures", options.IncludeTextures,
                "systems", options.IncludeSystemTree, "spaces", options.IncludeSpaceTree, "disciplines", options.IncludeDisciplineTree,
                "visibleCadGeometry", options.IncludeCadLinks, "gridCurves", options.IncludeGrids, "selected2DDwg", options.Include2DViews);
        }

        internal Dictionary<string, object> CountManifest()
        {
            return PackageWriter.Fields("textureFiles", textureFiles.Count, "systemGroups", systems.Count, "spaceGroups", spaces.Count,
                "disciplineGroups", disciplines.Count, "cadComponentRecords", cadCount, "gridCurves", grids.Count,
                "drawingViews", drawings.Count, "drawingFiles", drawingFileCount, "spaceUnassignedComponentRecords", spaceUnassigned);
        }

        internal string Summary()
        {
            return "贴图文件：" + textureFiles.Count + "；系统组：" + systems.Count + "；空间组：" + spaces.Count + "；专业组：" + disciplines.Count
                + "\nCAD 构件记录：" + cadCount + "；轴网：" + grids.Count + "；二维视图：" + drawings.Count + "（附件文件：" + drawingFileCount + "）";
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            // GetProperty(name) can throw AmbiguousMatchException for Room/Space
            // because the API also offers a Phase-indexed overload.
            PropertyInfo selected;
            lock (propertyCacheLock)
            {
                Type type = value.GetType();
                Dictionary<string, PropertyInfo> properties;
                if (!propertyCache.TryGetValue(type, out properties))
                {
                    properties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                    foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (property.GetIndexParameters().Length == 0 && !properties.ContainsKey(property.Name)) properties.Add(property.Name, property);
                    propertyCache.Add(type, properties);
                }
                if (!properties.TryGetValue(name, out selected)) return null;
            }
            try { return selected.GetValue(value, null); }
            catch (TargetInvocationException) { return null; }
        }
        private static string ReadString(object value, string name) { return Convert.ToString(ReadProperty(value, name), CultureInfo.InvariantCulture) ?? ""; }
        private static int ReadInt(object value, string name) { object number = ReadProperty(value, name); return number == null ? 0 : Convert.ToInt32(number, CultureInfo.InvariantCulture); }
        private void CheckCancel() { if (cancelled()) throw new OperationCanceledException(); }
        private void Warn(string message) { if (warnings.Add(message)) writer.Warnings.Add(message); }
        private sealed class DocumentIndex
        {
            internal readonly Dictionary<string, MEPSystem> Systems = new Dictionary<string, MEPSystem>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> Members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, SpatialElement> Spaces = new Dictionary<string, SpatialElement>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> TextureBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
