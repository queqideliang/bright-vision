// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilWallCutoutFixture
{
    private static int assertions;
    private static readonly List<string> cases = new List<string>();
    private const double Tolerance = 0.000001;
    private sealed class Fixture
    {
        internal Document Document = new Document();
        internal Wall Wall;
        internal FamilyInstance Instance;
        internal XYZ Origin = new XYZ(10, 20, 0), Axis = new XYZ(1, 0, 0), Normal = new XYZ(0, 1, 0), Direction = new XYZ(0, 3, 0);
        internal double BaseZ = 30, ToleranceFeet = Tolerance;
        internal CurveLoop Loop;
        internal Exception ProviderError;
        internal Action DuringProvider;
        internal int ProviderCalls;
        internal Fixture()
        {
            Wall = Document.Add(new Wall(), 10);
            Instance = Document.Add(new FamilyInstance { Host = Wall, Category = new Category { Id = new ElementId((long)BuiltInCategory.OST_Doors) } }, 20);
            Loop = Rectangle(12, 16, 21, 31, 36);
        }
        internal CurveLoop Provider(Document document, Wall wall, FamilyInstance instance, out XYZ direction)
        {
            ProviderCalls++;
            Require(Object.ReferenceEquals(document, Document) && Object.ReferenceEquals(wall, Wall) && Object.ReferenceEquals(instance, Instance), "Provider receives exact source objects");
            if (DuringProvider != null) DuringProvider();
            if (ProviderError != null) throw ProviderError;
            direction = Direction; return Loop;
        }
        internal bool Read(out CivilWallInsert cutout, out string reason)
        {
            return RevitWallCutoutReader.TryReadRectangleWithProvider(Document, Wall, Instance, Origin, Axis, Normal, BaseZ, ToleranceFeet, Provider, out cutout, out reason);
        }
    }
    private static CurveLoop Rectangle(double x0, double x1, double y, double z0, double z1)
    {
        return Polygon(new XYZ(x0, y, z0), new XYZ(x1, y, z0), new XYZ(x1, y, z1), new XYZ(x0, y, z1));
    }
    private static CurveLoop Polygon(params XYZ[] points)
    {
        var loop = new CurveLoop();
        for (int index = 0; index < points.Length; index++) loop.Add(Line.CreateBound(points[index], points[(index + 1) % points.Length]));
        return loop;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
    private static void Near(double actual, double expected, string message) { Require(Math.Abs(actual - expected) < 1e-10, message); }
    private static CivilWallInsert Accept(Fixture fixture, string name)
    {
        CivilWallInsert result; string reason;
        Require(fixture.Read(out result, out reason) && result != null && reason == String.Empty, name + ": " + reason);
        Require(result.Status == "recognized" && result.Kind == "rect-opening", name + " retains existing calculator whitelist");
        Require(fixture.Loop.DisposeCalls == 1, name + " disposes returned loop once");
        cases.Add(name); return result;
    }
    private static void Reject(Action<Fixture> change, string name, bool providerExpected)
    {
        var fixture = new Fixture(); change(fixture); CivilWallInsert result; string reason;
        Require(!fixture.Read(out result, out reason) && result == null && !String.IsNullOrEmpty(reason), name);
        Require((fixture.ProviderCalls != 0) == providerExpected, name + " validates identity/frame before provider");
        if (providerExpected && fixture.Loop != null && fixture.ProviderError == null) Require(fixture.Loop.DisposeCalls == 1, name + " disposes rejected returned loop");
        cases.Add(name);
    }

    private static int Main(string[] args)
    {
        string output = args[0];
        string reason;
        var fixture = new Fixture(); CivilWallInsert result = Accept(fixture, "actual-door-rectangle");
        Near(result.RectX0.Value, 2 * 0.3048, "Axis lower SI"); Near(result.RectX1.Value, 6 * 0.3048, "Axis upper SI");
        Near(result.RectZ0.Value, 0.3048, "Base-relative lower SI"); Near(result.RectZ1.Value, 6 * 0.3048, "Base-relative upper SI");
        Require(result.UniqueId == fixture.Instance.UniqueId && result.ElementId == "20" && result.HostUniqueId == fixture.Wall.UniqueId, "Exact native identity retained");
        Require(result.Source.Contains("GetInstanceCutoutFromWall") && result.Source.Contains("actual door cutout") && result.Source.Contains("host ElementId=10"), "Official API, actual category and host provenance retained");
        Require(Math.Abs(result.RectX1.Value-result.RectX0.Value-fixture.Instance.NominalWidthValue*0.3048)>1 && Math.Abs(result.RectZ1.Value-result.RectZ0.Value-fixture.Instance.NominalHeightValue*0.3048)>1,
            "Actual four-by-five-foot cutout differs from stored 1234-by-0.25-foot nominal values");
        fixture = new Fixture(); fixture.Instance.Category.Id = new ElementId((long)BuiltInCategory.OST_Windows); result = Accept(fixture, "actual-window-rectangle"); Require(result.Source.Contains("actual window cutout"), "Window category provenance");
        fixture = new Fixture(); fixture.Direction = new XYZ(0, -8, 0); Accept(fixture, "reverse-cut-direction");
        fixture = new Fixture();
        fixture.Loop = Polygon(new XYZ(12,21,31),new XYZ(12,21,36),new XYZ(16,21,36),new XYZ(16,21,31)); Accept(fixture, "clockwise-rectangle");
        fixture = new Fixture(); fixture.Loop = Rectangle(8, 11, 21, 28, 32); result = Accept(fixture, "outside-wall-domain-retained-for-clipping"); Near(result.RectX0.Value, -2 * 0.3048, "Negative axis coordinate not silently clipped");
        fixture = new Fixture(); fixture.Axis = new XYZ(0, 1, 0); fixture.Normal = new XYZ(-1, 0, 0); fixture.Direction = new XYZ(-2, 0, 0);
        fixture.Loop = Polygon(new XYZ(9,22,31),new XYZ(9,26,31),new XYZ(9,26,36),new XYZ(9,22,36)); result = Accept(fixture, "rotated-wall-local-frame"); Near(result.RectX1.Value,6*0.3048,"Rotated frame exact projection");
        Require(FamilyInstance.NominalReads == 0, "Different, throwing nominal dimension APIs never read");
        Reject(f => f.Loop = null, "no-actual-cutout", true);
        Reject(f => f.Loop = new CurveLoop(), "empty-loop", true);
        Reject(f => f.Loop.RemoveAt(3), "three-edges", true);
        Reject(f => f.Loop.Add(Line.CreateBound(new XYZ(12,21,31),new XYZ(13,21,31))), "more-than-four-edges", true);
        Reject(f => f.Loop[0] = new Arc(), "curved-edge-not-replaced-by-bounds", true);
        Reject(f => ((Line)f.Loop[0]).IsBound = false, "unbounded-line", true);
        Reject(f => ((Line)f.Loop[0]).B = new XYZ(15,21,31), "disconnected-loop", true);
        Reject(f => f.Loop = new CurveLoop { f.Loop[0], f.Loop[2], f.Loop[1], f.Loop[3] }, "wrong-edge-order", true);
        Reject(f => f.Loop = Polygon(new XYZ(12,21,31),new XYZ(16,21,32),new XYZ(16,21,36),new XYZ(12,21,35)), "four-lines-parallelogram", true);
        Reject(f => f.Loop = Polygon(new XYZ(12,21,31),new XYZ(16,21,31),new XYZ(16,21,36),new XYZ(13,21,36)), "four-lines-trapezoid", true);
        Reject(f => f.Loop = Polygon(new XYZ(12,21,31),new XYZ(16,21,31),new XYZ(12,21,31),new XYZ(12,21,36)), "retrace-and-duplicate-corner", true);
        Reject(f => f.Loop = Polygon(new XYZ(12,21,31),new XYZ(16,22,31),new XYZ(16,22,36),new XYZ(12,21,36)), "non-wall-parallel-plane", true);
        Reject(f => ((Line)f.Loop[0]).A = new XYZ(Double.NaN,21,31), "NaN-endpoint", true);
        Reject(f => ((Line)f.Loop[0]).B = new XYZ(16,Double.PositiveInfinity,31), "infinite-endpoint", true);
        Reject(f => f.Loop = Rectangle(12,12+Tolerance/2,21,31,36), "degenerate-narrow-rectangle", true);
        Reject(f => f.Direction = null, "missing-cut-direction", true);
        Reject(f => f.Direction = new XYZ(0,0,0), "zero-cut-direction", true);
        Reject(f => f.Direction = new XYZ(1,1,0), "oblique-cut-direction", true);
        Reject(f => f.Direction = new XYZ(0.0000001,1,0), "small-oblique-cut-direction", true);
        Reject(f => f.Direction = new XYZ(Double.NaN,1,0), "NaN-cut-direction", true);
        Reject(f => f.Origin = new XYZ(Double.NaN,0,0), "invalid-origin", false);
        Reject(f => f.Axis = new XYZ(2,0,0), "nonunit-axis", false);
        Reject(f => f.Normal = new XYZ(1,0,0), "nonorthogonal-frame", false);
        Reject(f => f.BaseZ = Double.PositiveInfinity, "invalid-base-elevation", false);
        Reject(f => f.ToleranceFeet = 0, "zero-tolerance", false);
        Reject(f => f.ToleranceFeet = Double.NaN, "NaN-tolerance", false);
        Reject(f => f.Instance.Category.Id = new ElementId(-99), "other-family-category", false);
        Reject(f => f.Instance.Category = null, "missing-category", false);
        Reject(f => f.Instance.Host = f.Document.Add(new Wall(),30), "wrong-host", false);
        Reject(f => f.Instance.Host = null, "missing-host", false);
        Reject(f => f.Instance.Document = new Document(), "foreign-instance-document", false);
        Reject(f => f.Wall.Document = new Document(), "foreign-wall-document", false);
        Reject(f => f.Document.Elements.Remove(20), "missing-live-instance", false);
        Reject(f => f.Document.Elements[20] = new FamilyInstance{UniqueId="substituted",IsValidObject=true}, "substituted-instance-identity", false);
        Reject(f => f.Document.IsFamilyDocument = true, "family-document", false);
        Reject(f => f.Wall.IsValidObject = false, "invalid-wall", false);
        Reject(f => f.Instance.UniqueId = String.Empty, "empty-instance-identity", false);
        Reject(f => f.DuringProvider = delegate { f.Instance.Host = f.Document.Add(new Wall(),30); }, "host-changes-during-read", true);
        Reject(f => f.ProviderError = new FileNotFoundException("controlled missing official IFC library"), "provider-library-unavailable", true);
        Reject(f => f.ProviderError = new InvalidOperationException("controlled native API failure"), "provider-failure-no-fallback", true);
        fixture = new Fixture(); fixture.ProviderError = new TargetInvocationException(new OperationCanceledException("fixture cancel"));
        try { fixture.Read(out result,out reason); throw new Exception("Cancellation swallowed"); } catch(OperationCanceledException) { assertions++; cases.Add("cancellation-propagates"); }
        fixture = new Fixture(); fixture.ProviderError = new OutOfMemoryException("controlled fixture");
        try { fixture.Read(out result,out reason); throw new Exception("OOM swallowed"); } catch(OutOfMemoryException) { assertions++; cases.Add("out-of-memory-propagates"); }

        MethodInfo method;
        Require(RevitWallCutoutReader.TryExactMethod(typeof(ExactMethod),out method),"Exact static/public/out/native-types signature accepted");
        Require(!RevitWallCutoutReader.TryExactMethod(typeof(WrongReturn),out method),"Wrong return rejected");
        Require(!RevitWallCutoutReader.TryExactMethod(typeof(RefInsteadOfOut),out method),"Ref without out metadata rejected");
        Require(!RevitWallCutoutReader.TryExactMethod(typeof(InstanceMethod),out method),"Instance API rejected");
        Require(!RevitWallCutoutReader.TryExactMethod(typeof(WrongParameter),out method),"Wrong parameter rejected");
        Require(!RevitWallCutoutReader.TryExactMethod(typeof(InheritedMethod),out method),"Inherited substitute method rejected");
        Require(!RevitWallCutoutReader.TryExactMethod(null,out method),"Missing API type rejected");
        cases.Add("official-method-signature-metadata");
        Assembly assembly;
        Require(!RevitWallCutoutReader.TryLoadOfficialAssembly(output,out assembly,out reason) && assembly == null && reason.Contains("未安装"),"Absent local official IFC library returns explicit unsupported");
        cases.Add("loader-missing-library");
        string wrongNameDirectory = Path.Combine(output,"wrong-name"); Directory.CreateDirectory(wrongNameDirectory);
        File.Copy(typeof(CivilWallCutoutFixture).Assembly.Location,Path.Combine(wrongNameDirectory,"RevitAPIIFC.dll"));
        Require(!RevitWallCutoutReader.TryLoadOfficialAssembly(wrongNameDirectory,out assembly,out reason) && assembly == null && reason.Contains("名称不匹配"),"Renaming an arbitrary managed fixture DLL to RevitAPIIFC does not pass identity checks");
        cases.Add("loader-rejects-wrong-assembly-name");
        string invalidDirectory = Path.Combine(output,"invalid-library"); Directory.CreateDirectory(invalidDirectory);
        File.WriteAllBytes(Path.Combine(invalidDirectory,"RevitAPIIFC.dll"),new byte[]{0,1,2});
        Require(!RevitWallCutoutReader.TryLoadOfficialAssembly(invalidDirectory,out assembly,out reason) && assembly == null && reason.Contains("无法核验"),"Invalid local IFC library returns unsupported without executing it");
        cases.Add("loader-invalid-library");
        fixture = new Fixture();
        Require(!RevitWallCutoutReader.TryReadRectangle(fixture.Document,fixture.Wall,fixture.Instance,fixture.Origin,fixture.Axis,fixture.Normal,fixture.BaseZ,fixture.ToleranceFeet,out result,out reason) && result == null,
            "Production entry does not use fixture provider or search arbitrary DLL paths");
        cases.Add("production-entry-without-Autodesk-host");
        string foreignDirectory=Path.Combine(output,"foreign");
        Require(RevitWallCutoutReader.TryLoadOfficialAssembly(foreignDirectory,out assembly,out reason) && assembly.GetName().Name=="RevitAPIIFC", "Location-check marker loads only from supplied verified test host directory");
        Require(RevitWallCutoutReader.TryLoadOfficialAssembly(foreignDirectory,out assembly,out reason), "Already loaded same-directory location-check marker is reused");
        Require(!RevitWallCutoutReader.TryExactMethod(assembly.GetType("FixtureIfcLocationMarker"),out method),"Matching assembly name/location alone does not supply an actual cutout API");
        Require(!RevitWallCutoutReader.TryLoadOfficialAssembly(output,out assembly,out reason) && assembly == null && reason.Contains("其他目录"),"Already loaded other-directory same-name fixture IFC assembly is rejected");
        cases.Add("loader-rejects-other-directory-same-name");
        Require(Curve.TessellateCalls == 0,"No native-curve tessellation used"); Require(Curve.DisposeCalls == 0,"Borrowed line wrappers never individually disposed"); Require(FamilyInstance.NominalReads == 0,"All negative paths avoid nominal parameters");
        File.WriteAllLines(Path.Combine(output,"cases.txt"),cases);
        Console.WriteLine("PASS " + assertions + " contract/adapter assertions; controlled project-owned curve API stand-ins only; no Autodesk host, IFC API execution or CSG proof.");
        return assertions;
    }
    public class ExactMethod
    {
        public static CurveLoop GetInstanceCutoutFromWall(Document d,Wall w,FamilyInstance f,out XYZ direction){direction=null;return null;}
    }
    public sealed class InheritedMethod : ExactMethod { }
    public sealed class WrongReturn
    {
        public static object GetInstanceCutoutFromWall(Document d,Wall w,FamilyInstance f,out XYZ direction){direction=null;return null;}
    }
    public sealed class RefInsteadOfOut
    {
        public static CurveLoop GetInstanceCutoutFromWall(Document d,Wall w,FamilyInstance f,ref XYZ direction){return null;}
    }
    public sealed class InstanceMethod
    {
        public CurveLoop GetInstanceCutoutFromWall(Document d,Wall w,FamilyInstance f,out XYZ direction){direction=null;return null;}
    }
    public sealed class WrongParameter
    {
        public static CurveLoop GetInstanceCutoutFromWall(Document d,Wall w,Element f,out XYZ direction){direction=null;return null;}
    }
}
