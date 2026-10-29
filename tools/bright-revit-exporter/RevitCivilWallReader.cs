// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Civil;
using BrightVision.Export;

namespace BrightVision.Revit
{
    // Read-only wall facts and constructive-solid consistency check. Neither
    // HOST_VOLUME_COMPUTED nor a mesh/bounding box is the geometry oracle.
    internal static class RevitCivilWallReader
    {
        private const double MetresPerFoot = 0.3048;
        private const double CubicMetresPerCubicFoot = MetresPerFoot * MetresPerFoot * MetresPerFoot;
        private const double CoordinateToleranceFeet = 0.000001 / MetresPerFoot;
        private const int MaximumInserts = 200;
        private const int MaximumOverlapCandidates = 64;
        private const int MaximumCatalogWalls = 20000;

        internal static CivilWallSnapshot Read(UIDocument ui, CivilSnapshot nativeSnapshot, CivilReadRequest request)
        { return Read(ui, nativeSnapshot, request, ui == null ? null : ui.ActiveView); }

        internal static CivilWallSnapshot Read(UIDocument ui, CivilSnapshot nativeSnapshot, CivilReadRequest request, View geometryView)
        { return Read(ui, nativeSnapshot, request, geometryView, CivilWallRuleTemplates.Default(), null); }

        internal static CivilWallSnapshot Read(UIDocument ui, CivilSnapshot nativeSnapshot, CivilReadRequest request, View geometryView, CivilWallRuleTemplate template, CivilWallReadControl control)
        {
            template = CivilWallRuleTemplates.Clone(template ?? CivilWallRuleTemplates.Default());
            if (control != null) control.Checkpoint("核对来源与规则", 0, 0);
            if (ui == null || ui.Document == null || !ui.Document.IsValidObject || ui.Document.IsFamilyDocument)
                throw new InvalidOperationException("请先打开有效的工程模型。");
            if (nativeSnapshot == null) throw new ArgumentNullException("nativeSnapshot");
            if (request == null) throw new ArgumentNullException("request");
            Document document = ui.Document;
            string documentIdentity = document.ProjectInformation == null ? document.Title : document.ProjectInformation.UniqueId;
            if (PackageWriter.Hash(documentIdentity).Substring(0, 24) != nativeSnapshot.DocumentScope ||
                !String.Equals(document.PathName ?? String.Empty, nativeSnapshot.SourcePath ?? String.Empty, StringComparison.OrdinalIgnoreCase) ||
                RevitCivilQuantityReader.Revision(document) != nativeSnapshot.Revision)
                throw new InvalidOperationException("来源工程或模型修订已变化，请先刷新原始量。");
            Phase phase = document.GetElement(ParseId(request.PhaseId)) as Phase;
            if (phase == null) throw new InvalidOperationException("墙体试算需要明确的当前工程阶段。");
            CivilWallSnapshot result = new CivilWallSnapshot { NativeSnapshot = nativeSnapshot, RuleTemplate = template };
            if (geometryView != null)
            {
                if (!geometryView.IsValidObject || !geometryView.Document.Equals(document))
                    throw new InvalidOperationException("几何校验视图不属于当前有效工程。");
                View currentView = document.GetElement(geometryView.UniqueId) as View;
                if (currentView == null || !currentView.IsValidObject || !currentView.Id.Equals(geometryView.Id))
                    throw new InvalidOperationException("几何校验视图已失效，请重新选择。");
                geometryView = currentView;
            }
            string contextIssue = GeometryContextIssue(document, geometryView, request);
            if (geometryView != null)
            {
                result.GeometryViewUniqueId = geometryView.UniqueId; result.GeometryViewName = geometryView.Name;
                Parameter viewPhase = geometryView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (viewPhase != null && viewPhase.HasValue && viewPhase.StorageType == StorageType.ElementId) result.GeometryPhaseId = Id(viewPhase.AsElementId());
            }
            WallCatalog catalog = contextIssue == null ? BuildWallCatalog(document, phase, control) : null;
            foreach (CivilElementRecord source in nativeSnapshot.Elements)
            {
                if (control != null) control.Checkpoint("读取墙体与切洞", result.Walls.Count, nativeSnapshot.Elements.Count);
                Element element = document.GetElement(source.UniqueId);
                Wall wall = element as Wall;
                if (wall == null && source.KindCode != "wall") continue;
                CivilWallFact fact = new CivilWallFact {
                    ElementId = source.ElementId, UniqueId = source.UniqueId, Name = source.Name,
                    NativeLength = source.Length, NativeArea = source.Area, NativeVolume = source.Volume
                };
                result.Walls.Add(fact);
                try
                {
                    if (wall == null)
                    { fact.Status = "failed"; fact.Issues.Add("原始量表中的墙来源已失效或不再是原生墙，未静默丢弃该来源。"); continue; }
                    if (!wall.IsValidObject || Id(wall.Id) != source.ElementId)
                    { Unsupported(fact, "原生墙身份已变化。"); continue; }
                    ReadWall(document, wall, source, phase, geometryView, contextIssue, catalog, fact, template, control);
                }
                catch (Exception error)
                {
                    if (IsCancellation(error) || error is OutOfMemoryException) throw;
                    fact.GeometryVerified = false; fact.Status = "failed";
                    fact.Issues.Add("墙体读取／布尔校验失败，未出试算量：" + error.GetType().Name + "：" + error.Message);
                }
            }
            if (control != null) control.Checkpoint("完成墙体事实", result.Walls.Count, result.Walls.Count);
            if (RevitCivilQuantityReader.Revision(document) != nativeSnapshot.Revision)
                throw new InvalidOperationException("墙体读取期间模型已变化，请刷新后重算；原结果未替换。");
            return result;
        }

        private static string GeometryContextIssue(Document document, View view, CivilReadRequest request)
        {
            if (request.DesignOptionId != "-1") return "本轮墙体几何试算只支持主模型，不混合设计选项。";
            if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                return "请先切到相同阶段的模型视图，再读取墙体试算；图纸／明细表不作为几何上下文。";
            if (!FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
                return "所选几何校验视图不能迭代模型构件，未使用该视图证明试算。";
            Parameter parameter = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.ElementId || Id(parameter.AsElementId()) != request.PhaseId)
                return "当前模型视图阶段与本次读取阶段不一致，未使用其他阶段的实体证明试算。";
            return null;
        }

        private static void ReadWall(Document document, Wall wall, CivilElementRecord source, Phase phase, View view, string contextIssue, WallCatalog catalog, CivilWallFact fact, CivilWallRuleTemplate template, CivilWallReadControl control)
        {
            WallType type = wall.WallType;
            if (type != null) { fact.TypeUniqueId = type.UniqueId; fact.TypeName = type.Name; }
            if (!source.Supported || type == null || type.Kind != WallKind.Basic || wall.IsStackedWall || wall.IsStackedWallMember)
            { Unsupported(fact, "本轮仅支持原始量表已确认的普通基本墙，叠层／幕墙及其成员不参与试算。"); return; }
            ElementOnPhaseStatus wallPhase = wall.GetPhaseStatus(phase.Id);
            if (wall.DesignOption != null || (wallPhase != ElementOnPhaseStatus.Existing && wallPhase != ElementOnPhaseStatus.New))
            { Unsupported(fact, "原生墙不属于本次确认阶段的现有／新建主模型范围。"); return; }
            fact.ParameterLength = Length(wall, BuiltInParameter.CURVE_ELEM_LENGTH);
            fact.ParameterHeight = Length(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            fact.ParameterWidth = ScalarLength(type.Width, "WallType.Width");
            fact.AttachedTop = Attached(wall, BuiltInParameter.WALL_TOP_IS_ATTACHED);
            fact.AttachedBottom = Attached(wall, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED);
            Parameter top = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            fact.TopConstraintId = top != null && top.HasValue && top.StorageType == StorageType.ElementId ? Id(top.AsElementId()) : String.Empty;
            if (!fact.AttachedTop.HasValue || !fact.AttachedBottom.HasValue || fact.TopConstraintId.Length == 0)
            { Unsupported(fact, "顶底附着／顶部约束参数不可确认，未推断矩形基准。"); return; }
            if (fact.AttachedTop.Value || fact.AttachedBottom.Value || fact.TopConstraintId != "-1")
            { Unsupported(fact, "附着顶底或顶部连接墙不使用未连接高度试算。"); return; }
            if (!Positive(fact.ParameterLength) || !Positive(fact.ParameterHeight) || !Positive(fact.ParameterWidth))
            { Unsupported(fact, "长度、未连接高度或类型厚度缺失／非正数，未构造矩形基准。"); return; }
            Parameter baseParameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Level baseLevel = baseParameter != null && baseParameter.HasValue && baseParameter.StorageType == StorageType.ElementId
                ? document.GetElement(baseParameter.AsElementId()) as Level : null;
            CivilMetric offset = Length(wall, BuiltInParameter.WALL_BASE_OFFSET);
            if (baseLevel == null || offset.Status != "available" || !Finite(baseLevel.ProjectElevation))
            { Unsupported(fact, "基底楼层或底部偏移不可读，未按包围盒猜测基底。"); return; }
            double baseZ = baseLevel.ProjectElevation + offset.Value.Value / MetresPerFoot;
            if (!Finite(baseZ)) { Unsupported(fact, "墙基底标高无效。"); return; }
            fact.BaseElevationMetres = baseZ * MetresPerFoot;
            fact.BaseElevationSource = "Level.ProjectElevation + WALL_BASE_OFFSET";
            LocationCurve location = wall.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            fact.CurveKind = curve == null ? "missing" : curve.GetType().Name;
            Line line = curve as Line;
            if (line == null || !line.IsBound || Math.Abs(line.Direction.Z) > 1e-9 || !Finite(line.Length) || line.Length <= 0)
            { Unsupported(fact, "仅水平直线墙轴可构造当前矩形基准；弧墙、斜轴和无界轴未支持。"); return; }
            double length = fact.ParameterLength.Value.Value / MetresPerFoot;
            double height = fact.ParameterHeight.Value.Value / MetresPerFoot;
            double width = fact.ParameterWidth.Value.Value / MetresPerFoot;
            if (Math.Abs(line.Length - length) > CoordinateToleranceFeet)
            { Unsupported(fact, "实例长度与原生直线轴长度不一致，未静默选择一种长度。"); return; }
            XYZ origin = line.GetEndPoint(0), axis = line.Direction, normal = new XYZ(-axis.Y, axis.X, 0);
            foreach (ElementId id in JoinGeometryUtils.GetJoinedElements(document, wall))
                if (!id.Equals(wall.Id)) fact.JoinedElementIds.Add(Id(id));
            for (int end = 0; end < 2; end++)
                foreach (Element joined in location.get_ElementsAtJoin(end))
                    if (joined != null && !joined.Id.Equals(wall.Id) && !fact.EndpointJoinedElementIds.Contains(Id(joined.Id)))
                        fact.EndpointJoinedElementIds.Add(Id(joined.Id));
            if (!ReadInserts(document, wall, phase, origin, axis, normal, baseZ, fact, template, control)) return;
            if (contextIssue != null) { Unsupported(fact, contextIssue); return; }
            if (fact.JoinedElementIds.Count > 0 || fact.EndpointJoinedElementIds.Count > 0)
            { Unsupported(fact, "存在显式几何连接或墙端交接，本轮不猜测交接增扣顺序。"); return; }
            if (fact.Inserts.Any(insert => insert.Status == "unsupported"))
            { Unsupported(fact, "存在未可靠识别的宿主插入／门窗实际切洞，本轮未将名义尺寸当实际洞口。"); return; }
            VerifyGeometry(wall, view, origin, axis, normal, baseZ, length, height, width, catalog, fact, template, control);
        }

        internal static bool ReadConfirmedInserts(Document document, Wall wall, Phase phase, XYZ origin, XYZ axis, XYZ normal, double baseZ, CivilWallFact fact, CivilWallRuleTemplate template, CivilWallReadControl control)
        { return ReadInserts(document, wall, phase, origin, axis, normal, baseZ, fact, template, control); }

        private static bool ReadInserts(Document document, Wall wall, Phase phase, XYZ origin, XYZ axis, XYZ normal, double baseZ, CivilWallFact fact, CivilWallRuleTemplate template, CivilWallReadControl control)
        {
            IList<ElementId> ids = wall.FindInserts(true, true, true, true).ToList();
            if (ids.Count > MaximumInserts) { Unsupported(fact, "单墙宿主插入超过 200 个，未截断后继续试算。"); return false; }
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ElementId id in ids)
            {
                if (control != null) control.Checkpoint("读取宿主切洞", fact.Inserts.Count, ids.Count);
                if (!seen.Add(Id(id))) continue;
                Element element = document.GetElement(id);
                CivilWallInsert item = new CivilWallInsert { ElementId = Id(id), Source = "HostObject.FindInserts(true,true,true,true)" };
                fact.Inserts.Add(item);
                if (element == null || !element.IsValidObject) { item.Issues.Add("宿主插入已失效。"); continue; }
                item.UniqueId = element.UniqueId; item.Name = element.Name;
                Opening opening = element as Opening;
                FamilyInstance family = element as FamilyInstance;
                long category = element.Category == null ? 0 : NumericId(element.Category.Id);
                item.Kind = opening != null ? "rect-opening" : category == (long)BuiltInCategory.OST_Doors ? "door" : category == (long)BuiltInCategory.OST_Windows ? "window" : "other";
                Element host = opening != null ? opening.Host : family != null ? family.Host : null;
                item.HostUniqueId = host == null ? String.Empty : host.UniqueId;
                if (host == null || !host.Document.Equals(document) || !host.Id.Equals(wall.Id))
                { item.Issues.Add("插入未明确指向当前根文档墙宿主。"); continue; }
                // Non-phaseable native openings have no independent creation /
                // demolition lifecycle. Their exact host and selected-phase
                // host geometry are the explicit evidence; do not invent a
                // CreatedPhaseId or treat a phaseable unknown status as current.
                bool ownPhases = element.HasPhases();
                ElementOnPhaseStatus status = ownPhases ? element.GetPhaseStatus(phase.Id) : wall.GetPhaseStatus(phase.Id);
                if ((status != ElementOnPhaseStatus.New && status != ElementOnPhaseStatus.Existing) || element.DesignOption != null)
                { item.Status = "excluded"; item.Issues.Add("插入不属于本次现有／新建阶段与主模型范围。"); continue; }
                if (opening == null)
                {
                    if (family != null && template.OpeningPolicy == CivilWallRuleTemplates.VerifiedRectCutout &&
                        (category == (long)BuiltInCategory.OST_Doors || category == (long)BuiltInCategory.OST_Windows))
                    {
                        CivilWallInsert verified; string reason;
                        if (RevitWallCutoutReader.TryReadRectangle(document, wall, family, origin, axis, normal, baseZ, CoordinateToleranceFeet, out verified, out reason))
                        {
                            verified.Source += "; confirmed host + selected phase/main model; final wall solid consistency required";
                            fact.Inserts[fact.Inserts.Count - 1] = verified;
                        }
                        else item.Issues.Add(reason);
                    }
                    else item.Issues.Add("本规则未接纳该门窗／其他族的实际切洞；不以类型宽高代替 void。");
                    continue;
                }
                if (!opening.IsRectBoundary) { item.Kind = "other"; item.Issues.Add("非矩形 Opening 尚未支持。"); continue; }
                IList<XYZ> points = opening.BoundaryRect;
                if (points == null || points.Count != 2 || points.Any(point => point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z)))
                { item.Issues.Add("矩形 Opening 边界不是两个有效原生点。"); continue; }
                XYZ diagonal = points[1] - points[0];
                if (Math.Abs(diagonal.DotProduct(normal)) > CoordinateToleranceFeet)
                { item.Issues.Add("Opening 边界不在墙的垂直轴向平面内。"); continue; }
                double x0 = (points[0] - origin).DotProduct(axis), x1 = (points[1] - origin).DotProduct(axis);
                double z0 = points[0].Z - baseZ, z1 = points[1].Z - baseZ;
                if (Math.Abs(x1 - x0) <= CoordinateToleranceFeet || Math.Abs(z1 - z0) <= CoordinateToleranceFeet)
                { item.Issues.Add("Opening 矩形宽或高过小／退化。"); continue; }
                item.RectX0 = Math.Min(x0, x1) * MetresPerFoot; item.RectX1 = Math.Max(x0, x1) * MetresPerFoot;
                item.RectZ0 = Math.Min(z0, z1) * MetresPerFoot; item.RectZ1 = Math.Max(z0, z1) * MetresPerFoot;
                item.CutoutMethod = "native-opening";
                item.Source = "Opening.Host + Opening.IsRectBoundary + Opening.BoundaryRect; projected to wall axis and base Z; "
                    + (ownPhases ? "Opening.GetPhaseStatus" : "Opening.HasPhases=false; confirmed host phase and geometry view");
                item.Status = "recognized";
            }
            return true;
        }

        private static void VerifyGeometry(Wall wall, View view, XYZ origin, XYZ axis, XYZ normal, double baseZ, double length, double height, double width, WallCatalog catalog, CivilWallFact fact, CivilWallRuleTemplate template, CivilWallReadControl control)
        {
            List<Solid> owned = new List<Solid>();
            try
            {
                using (Options options = new Options())
                {
                    options.ComputeReferences = false; options.IncludeNonVisibleObjects = false; options.View = view;
                    using (GeometryElement geometry = wall.get_Geometry(options))
                    {
                        if (geometry == null) { Unsupported(fact, "当前模型视图未提供墙实体，未以原生参数体积代替几何校验。"); return; }
                        List<Solid> solids = new List<Solid>();
                        foreach (GeometryObject obj in geometry)
                        {
                            if (obj is GeometryInstance || obj is Mesh) { Unsupported(fact, "墙几何含未支持的实例变换层或网格分支，未忽略该分支。"); return; }
                            Solid solid = obj as Solid;
                            if (solid == null) continue;
                            if (!Finite(solid.Volume) || solid.Volume < 0) throw new InvalidOperationException("墙实体体积非有限或负数。");
                            if (solid.Faces.Size > 0 && solid.Volume > 1e-12) solids.Add(solid);
                        }
                        if (solids.Count == 0) { Unsupported(fact, "当前几何未提供可核对的正体积实体。"); return; }
                        Solid actual = solids[0];
                        for (int index = 1; index < solids.Count; index++) actual = Boolean(actual, solids[index], BooleanOperationsType.Union, owned, control);
                        fact.GeometryVolumeMetres3 = CheckedVolume(actual) * CubicMetresPerCubicFoot;
                        double minimumN = Double.PositiveInfinity, maximumN = Double.NegativeInfinity;
                        foreach (Solid solid in solids)
                            foreach (Edge edge in solid.Edges)
                            {
                                Curve edgeCurve = edge.AsCurve();
                                foreach (XYZ point in edgeCurve.Tessellate())
                                {
                                    double n = (point - origin).DotProduct(normal);
                                    if (!Finite(n)) throw new InvalidOperationException("墙实体横向坐标无效。");
                                    minimumN = Math.Min(minimumN, n); maximumN = Math.Max(maximumN, n);
                                }
                            }
                        if (!Finite(minimumN) || !Finite(maximumN) || Math.Abs(maximumN - minimumN - width) > CoordinateToleranceFeet)
                        { Unsupported(fact, "实际实体厚度范围与类型厚度不一致，未把斜墙、变截面或附加几何当矩形墙。"); return; }
                        Solid expected = Own(Prism(origin, axis, normal, 0, length, minimumN, minimumN + width, baseZ, height), owned);
                        double prismMetres3 = CheckedVolume(expected) * CubicMetresPerCubicFoot;
                        fact.GeometryToleranceMetres3 = Math.Max(template.AbsoluteToleranceMetres3, prismMetres3 * template.RelativeTolerance);
                        foreach (CivilWallInsert insert in fact.Inserts.Where(item => item.Status == "recognized"))
                        {
                            if (control != null) control.Checkpoint("扣除已确认切洞实体", 0, fact.Inserts.Count);
                            double x0 = insert.RectX0.Value / MetresPerFoot, x1 = insert.RectX1.Value / MetresPerFoot;
                            double z0 = insert.RectZ0.Value / MetresPerFoot, z1 = insert.RectZ1.Value / MetresPerFoot;
                            // Boolean differences naturally clip at the wall boundary
                            // and subtract overlapping openings only once.
                            Solid cutter = Own(Prism(origin, axis, normal, x0, x1, minimumN - CoordinateToleranceFeet * 10, maximumN + CoordinateToleranceFeet * 10, baseZ + z0, z1 - z0), owned);
                            expected = Boolean(expected, cutter, BooleanOperationsType.Difference, owned, control);
                        }
                        Solid excess = Boolean(expected, actual, BooleanOperationsType.Difference, owned, control);
                        if (control != null) control.Checkpoint("核验实际墙体形状", 0, 2);
                        Solid missing = Boolean(actual, expected, BooleanOperationsType.Difference, owned, control);
                        double difference = (CheckedVolume(excess) + CheckedVolume(missing)) * CubicMetresPerCubicFoot;
                        fact.SymmetricDifferenceMetres3 = difference;
                        if (difference > fact.GeometryToleranceMetres3.Value)
                        { Unsupported(fact, "参数矩形棱柱扣明确矩形洞口后，与实际墙实体不一致；可能存在编辑轮廓、未知切割或其他形状影响。"); return; }
                        if (!CheckInterwallOverlap(wall, view, actual, catalog, fact, control)) return;
                        fact.GeometryVerified = true; fact.Status = "verified";
                    }
                }
            }
            finally { foreach (Solid solid in owned) solid.Dispose(); }
        }

        // Bounds only generate possible pairs. They never establish overlap,
        // a deduction, or a result quantity. Actual view-context solids decide.
        private sealed class WallBox
        {
            internal Wall Wall;
            internal string ElementId;
            internal Bounds Bounds;
            internal string Issue;
        }
        private sealed class Bounds
        {
            internal double X0, X1, Y0, Y1, Z0, Z1;
            internal bool Overlaps(Bounds other)
            {
                double padding = CoordinateToleranceFeet * 10;
                return X1 + padding >= other.X0 && other.X1 + padding >= X0 &&
                    Y1 + padding >= other.Y0 && other.Y1 + padding >= Y0 && Z1 + padding >= other.Z0 && other.Z1 + padding >= Z0;
            }
        }
        private sealed class WallCatalog
        {
            internal string Issue;
            internal readonly List<WallBox> Walls = new List<WallBox>();
        }
        private static WallCatalog BuildWallCatalog(Document document, Phase phase, CivilWallReadControl control)
        {
            WallCatalog catalog = new WallCatalog();
            try
            {
                List<Wall> walls;
                using (FilteredElementCollector collector = new FilteredElementCollector(document).OfClass(typeof(Wall)))
                    walls = collector.Cast<Wall>().Take(MaximumCatalogWalls + 1).ToList();
                if (walls.Count > MaximumCatalogWalls)
                { catalog.Issue = "根文档墙目录超过 20,000 个，本轮没有截断后宣称墙间检查完整。"; return catalog; }
                foreach (Wall other in walls)
                {
                    if (control != null && catalog.Walls.Count % 32 == 0) control.Checkpoint("读取同阶段墙体依赖目录", catalog.Walls.Count, walls.Count);
                    if (other == null || !other.IsValidObject)
                    { catalog.Issue = "墙目录含失效对象，无法证明本次墙体独立性。"; return catalog; }
                    WallBox entry = new WallBox { Wall = other, ElementId = Id(other.Id) };
                    try
                    {
                        if (other.DesignOption != null) continue;
                        ElementOnPhaseStatus status = other.GetPhaseStatus(phase.Id);
                        if (status != ElementOnPhaseStatus.New && status != ElementOnPhaseStatus.Existing) continue;
                    }
                    catch (Exception error)
                    {
                        if (IsCancellation(error) || error is OutOfMemoryException) throw;
                        entry.Issue = "候选墙的阶段／设计选项无法确认：" + error.GetType().Name;
                    }
                    try { entry.Bounds = ReadBounds(other); }
                    catch (Exception error)
                    {
                        if (IsCancellation(error) || error is OutOfMemoryException) throw;
                        entry.Issue = "候选墙全模型包围盒不可读，未据此排除潜在重叠：" + error.GetType().Name;
                    }
                    catalog.Walls.Add(entry);
                }
            }
            catch (Exception error)
            {
                if (IsCancellation(error) || error is OutOfMemoryException) throw;
                catalog.Issue = "墙目录读取未完成：" + error.GetType().Name;
            }
            return catalog;
        }
        private static Bounds ReadBounds(Wall wall)
        {
            using (BoundingBoxXYZ box = wall.get_BoundingBox(null))
            {
                if (box == null) throw new InvalidOperationException("无模型包围盒。");
                XYZ min = box.Min, max = box.Max;
                if (min == null || max == null || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                    throw new InvalidOperationException("模型包围盒边界无效。");
                Bounds result = new Bounds { X0 = Double.PositiveInfinity, Y0 = Double.PositiveInfinity, Z0 = Double.PositiveInfinity,
                    X1 = Double.NegativeInfinity, Y1 = Double.NegativeInfinity, Z1 = Double.NegativeInfinity };
                Transform transform = box.Transform;
                if (transform == null) throw new InvalidOperationException("模型包围盒变换不可读。");
                foreach (double x in new[] { min.X, max.X }) foreach (double y in new[] { min.Y, max.Y }) foreach (double z in new[] { min.Z, max.Z })
                {
                    XYZ point = transform.OfPoint(new XYZ(x, y, z));
                    if (point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z)) throw new InvalidOperationException("模型包围盒世界坐标无效。");
                    result.X0 = Math.Min(result.X0, point.X); result.X1 = Math.Max(result.X1, point.X);
                    result.Y0 = Math.Min(result.Y0, point.Y); result.Y1 = Math.Max(result.Y1, point.Y);
                    result.Z0 = Math.Min(result.Z0, point.Z); result.Z1 = Math.Max(result.Z1, point.Z);
                }
                return result;
            }
        }
        private static bool CheckInterwallOverlap(Wall wall, View view, Solid actual, WallCatalog catalog, CivilWallFact fact, CivilWallReadControl control)
        {
            if (catalog == null || catalog.Issue != null)
            { Unsupported(fact, catalog == null ? "缺少同阶段主模型墙目录，墙间检查未完成。" : catalog.Issue); return false; }
            WallBox source = catalog.Walls.FirstOrDefault(entry => entry.ElementId == fact.ElementId);
            if (source == null || source.Bounds == null || source.Issue != null)
            { Unsupported(fact, "当前墙的全模型包围盒／阶段目录不可确认，墙间检查未完成。"); return false; }
            List<WallBox> candidates = catalog.Walls.Where(entry => entry.ElementId != fact.ElementId &&
                (entry.Bounds == null || source.Bounds.Overlaps(entry.Bounds))).OrderBy(entry => entry.ElementId, StringComparer.Ordinal).ToList();
            fact.OverlapCandidateCount = candidates.Count;
            if (candidates.Count > MaximumOverlapCandidates)
            { Unsupported(fact, "同阶段主模型墙重叠候选超过 64 个，未截断后继续出量。"); return false; }
            foreach (WallBox candidate in candidates)
            {
                if (control != null) control.Checkpoint("核对相邻墙体", 0, candidates.Count);
                if (candidate.Issue != null || candidate.Bounds == null)
                { Unsupported(fact, "墙间检查未完成，ElementId=" + candidate.ElementId + "：" + candidate.Issue); return false; }
                List<Solid> owned = new List<Solid>();
                try
                {
                    using (Options options = new Options())
                    {
                        options.ComputeReferences = false; options.IncludeNonVisibleObjects = false; options.View = view;
                        using (GeometryElement geometry = candidate.Wall.get_Geometry(options))
                        {
                            if (geometry == null) throw new InvalidOperationException("候选墙在校验视图中无可读实体。");
                            List<Solid> solids = new List<Solid>();
                            foreach (GeometryObject obj in geometry)
                            {
                                if (obj is GeometryInstance || obj is Mesh) throw new InvalidOperationException("候选墙含未支持的实例变换／网格分支。");
                                Solid solid = obj as Solid;
                                if (solid == null) continue;
                                double volume = CheckedVolume(solid);
                                if (solid.Faces.Size > 0 && volume > 1e-12) solids.Add(solid);
                            }
                            if (solids.Count == 0) throw new InvalidOperationException("候选墙未提供正体积实体。");
                            Solid other = solids[0];
                            for (int index = 1; index < solids.Count; index++) other = Boolean(other, solids[index], BooleanOperationsType.Union, owned, control);
                            Solid intersection = Boolean(actual, other, BooleanOperationsType.Intersect, owned, control);
                            double overlap = CheckedVolume(intersection) * CubicMetresPerCubicFoot;
                            if (overlap > fact.GeometryToleranceMetres3.Value) fact.IntersectingWallElementIds.Add(candidate.ElementId);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (IsCancellation(error) || error is OutOfMemoryException) throw;
                    Unsupported(fact, "墙间实体交集检查未完成，ElementId=" + candidate.ElementId + "：" + error.GetType().Name + "：" + error.Message); return false;
                }
                finally { foreach (Solid solid in owned) solid.Dispose(); }
            }
            fact.InterwallOverlapCheckComplete = true;
            if (fact.IntersectingWallElementIds.Count > 0)
            { Unsupported(fact, "与同阶段主模型其他墙存在超过容差的真实实体体积交叠，未计算交接扣减、未将重复体积相加。"); return false; }
            return true;
        }

        internal static Solid CreateParameterPrism(XYZ origin, XYZ axis, XYZ normal, double x0, double x1, double n0, double n1, double baseZ, double height)
        { return Prism(origin, axis, normal, x0, x1, n0, n1, baseZ, height); }

        private static Solid Prism(XYZ origin, XYZ axis, XYZ normal, double x0, double x1, double n0, double n1, double baseZ, double height)
        {
            XYZ a = new XYZ(origin.X + axis.X * x0 + normal.X * n0, origin.Y + axis.Y * x0 + normal.Y * n0, baseZ);
            XYZ b = new XYZ(origin.X + axis.X * x1 + normal.X * n0, origin.Y + axis.Y * x1 + normal.Y * n0, baseZ);
            XYZ c = new XYZ(origin.X + axis.X * x1 + normal.X * n1, origin.Y + axis.Y * x1 + normal.Y * n1, baseZ);
            XYZ d = new XYZ(origin.X + axis.X * x0 + normal.X * n1, origin.Y + axis.Y * x0 + normal.Y * n1, baseZ);
            CurveLoop loop = new CurveLoop();
            try
            {
                loop.Append(Line.CreateBound(a, b)); loop.Append(Line.CreateBound(b, c)); loop.Append(Line.CreateBound(c, d)); loop.Append(Line.CreateBound(d, a));
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, height);
            }
            finally { loop.Dispose(); }
        }
        private static Solid Own(Solid solid, IList<Solid> owned) { if (solid == null) throw new InvalidOperationException("布尔操作未返回实体。"); owned.Add(solid); return solid; }
        private static Solid Boolean(Solid left, Solid right, BooleanOperationsType operation, IList<Solid> owned, CivilWallReadControl control)
        {
            if (control != null) control.Checkpoint("实体布尔操作前", 0, 1);
            Solid result = Own(BooleanOperationsUtils.ExecuteBooleanOperation(left, right, operation), owned);
            // Register ownership before pumping input, so cancellation also
            // releases the just-computed native solid in the caller's finally.
            if (control != null) control.Checkpoint("实体布尔操作后", 1, 1);
            return result;
        }
        private static double CheckedVolume(Solid solid) { double value = solid.Volume; if (!Finite(value) || value < 0) throw new InvalidOperationException("几何校验体积无效。"); return value; }
        private static bool Positive(CivilMetric metric) { return metric != null && metric.Status == "available" && metric.Value.HasValue && metric.Value.Value > 0; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static CivilMetric ScalarLength(double raw, string source)
        { return new CivilMetric { Status = Finite(raw) ? "available" : "invalid", Value = Finite(raw) ? (double?)(raw * MetresPerFoot) : null, Unit = "m", Source = source }; }
        private static CivilMetric Length(Element element, BuiltInParameter key)
        {
            Parameter parameter = element.get_Parameter(key);
            if (parameter == null || !parameter.HasValue) return new CivilMetric { Unit = "m", Source = key.ToString(), Status = "missing" };
            if (parameter.StorageType != StorageType.Double) return new CivilMetric { Unit = "m", Source = key.ToString(), Status = "invalid" };
            return ScalarLength(parameter.AsDouble(), key.ToString());
        }
        private static bool? Attached(Wall wall, BuiltInParameter key)
        {
            Parameter parameter = wall.get_Parameter(key);
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Integer) return null;
            int value = parameter.AsInteger(); return value == 0 ? (bool?)false : value == 1 ? (bool?)true : null;
        }
        private static void Unsupported(CivilWallFact fact, string issue) { fact.Status = "unsupported"; fact.GeometryVerified = false; fact.Issues.Add(issue); }
        private static bool IsCancellation(Exception error) { return error is OperationCanceledException || error.GetType().FullName == "Autodesk.Revit.Exceptions.OperationCanceledException"; }
        private static ElementId ParseId(string value)
        {
#if REVIT_2024_OR_GREATER
            long number; if (!Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) throw new InvalidOperationException("阶段标识无效。"); return new ElementId(number);
#else
            int number; if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) throw new InvalidOperationException("阶段标识无效。"); return new ElementId(number);
#endif
        }
        private static long NumericId(ElementId id)
        {
#if REVIT_2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
        private static string Id(ElementId id) { return id == null ? "-1" : NumericId(id).ToString(CultureInfo.InvariantCulture); }
    }
}
