// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Civil;
using BrightVision.Export;

namespace BrightVision.Revit
{
    // Prepared native operands live only for this disposable read/compute
    // session. Report/session DTOs must copy Evidence, never this bundle.
    internal sealed class RevitWallJoinReadBundle : IDisposable
    {
        internal readonly List<Solid> Owned = new List<Solid>();
        public CivilSnapshot NativeSnapshot { get; internal set; }
        public string GeometryViewUniqueId { get; internal set; }
        public string GeometryViewName { get; internal set; }
        public string GeometryPhaseId { get; internal set; }
        public CivilWallRuleTemplate RuleTemplate { get; internal set; }
        public List<RevitWallJoinPreparedComponent> Components { get; internal set; }
        public List<string> Notices { get; internal set; }
        public int CatalogWallCount { get; internal set; }
        public int ExcludedUnknownGrossEnvelopeCount { get; internal set; }
        public ICivilWallJoinSolidOps Operations { get; internal set; }
        public bool IsDisposed { get; private set; }
        internal RevitWallJoinReadBundle() { Components = new List<RevitWallJoinPreparedComponent>(); Notices = new List<string>(); }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Exception first = null;
            for (int index = Owned.Count - 1; index >= 0; index--)
                try { Owned[index].Dispose(); } catch (Exception error) { if (first == null) first = error; }
            Owned.Clear();
            if (first != null) throw new InvalidOperationException("交接读取实体释放失败。", first);
        }
    }

    internal sealed class RevitWallJoinPreparedComponent
    {
        public string ComponentId { get; internal set; }
        public CivilWallJoinBinding Binding { get; internal set; }
        public List<CivilWallJoinParticipant> Participants { get; internal set; }
        public List<CivilWallJoinWallEvidence> Evidence { get; internal set; }
        public CivilWallJoinInput Input { get; internal set; }
        public string Status { get; internal set; }
        public string Message { get; internal set; }
    }

    // The core borrows the bundle's operands; all outputs of these operations
    // are independent objects whose lifetime is then managed by the core.
    internal sealed class NativeWallJoinSolidOps : ICivilWallJoinSolidOps
    {
        public object Clone(object value) { return RevitWallJoinReader.CopySolid(Require(value)); }
        public object Union(object left, object right) { return Operate(left, right, BooleanOperationsType.Union); }
        public object Difference(object left, object right) { return Operate(left, right, BooleanOperationsType.Difference); }
        public object Intersect(object left, object right) { return Operate(left, right, BooleanOperationsType.Intersect); }
        public double VolumeMetres3(object value) { return RevitWallJoinReader.Volume(Require(value)); }
        public void Dispose(object value) { Require(value).Dispose(); }
        private static Solid Require(object value)
        { Solid solid = value as Solid; if (solid == null) throw new InvalidOperationException("交接操作数不是当前宿主的原生实体。"); return solid; }
        private static Solid Operate(object left, object right, BooleanOperationsType operation)
        { Solid result = BooleanOperationsUtils.ExecuteBooleanOperation(Require(left), Require(right), operation); if (result == null) throw new InvalidOperationException("原生布尔操作没有返回实体。"); return result; }
    }

    internal static class RevitWallJoinReader
    {
        private const double M = 0.3048;
        private const double M3 = M * M * M;
        private const double CoordinateToleranceFeet = 0.000001 / M;
        private const int MaximumCatalogWalls = 20000;
        private const int MaximumCandidates = 64;
        private const int MaximumComponentWalls = 64;
        private const int MaximumNativeOperations = 20000;

        private sealed class Bounds
        {
            internal double X0, X1, Y0, Y1, Z0, Z1;
            internal double[] Values() { return new[] { X0, X1, Y0, Y1, Z0, Z1 }; }
            internal bool MayOverlap(Bounds other)
            {
                double pad = CoordinateToleranceFeet * 10;
                return X1 + pad >= other.X0 && other.X1 + pad >= X0 && Y1 + pad >= other.Y0 && other.Y1 + pad >= Y0 && Z1 + pad >= other.Z0 && other.Z1 + pad >= Z0;
            }
            internal static Bounds Join(Bounds left, Bounds right)
            {
                return new Bounds { X0 = Math.Min(left.X0,right.X0), X1 = Math.Max(left.X1,right.X1), Y0 = Math.Min(left.Y0,right.Y0),
                    Y1 = Math.Max(left.Y1,right.Y1), Z0 = Math.Min(left.Z0,right.Z0), Z1 = Math.Max(left.Z1,right.Z1) };
            }
        }
        private sealed class Entry
        {
            internal Wall Wall;
            internal string Id, Uid, CatalogIssue, JoinIssue;
            internal Bounds ActualBounds, CandidateBounds;
            internal readonly HashSet<string> Explicit = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> JoinedIds = new List<string>(), EndpointIds = new List<string>();
        }
        private sealed class Prepared
        {
            internal Entry Entry;
            internal CivilWallJoinWallEvidence Evidence;
            internal CivilWallJoinNode Node;
            internal Solid Expected, Actual, Reach;
            internal readonly HashSet<string> Explicit = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Related = new HashSet<string>(StringComparer.Ordinal);
            internal double Tolerance;
            internal string Issue;
        }
        private sealed class ReadState
        {
            internal Document Document;
            internal Phase Phase;
            internal View3D View;
            internal CivilWallRuleTemplate Template;
            internal CivilWallReadControl Control;
            internal RevitWallJoinReadBundle Bundle;
            internal Dictionary<string,Entry> Catalog;
            internal Dictionary<string,Prepared> Cache = new Dictionary<string,Prepared>(StringComparer.Ordinal);
            internal Dictionary<string,CivilElementRecord> Requested;
            internal int NativeOperations;
        }

        internal static RevitWallJoinReadBundle Read(UIDocument ui, CivilSnapshot nativeSnapshot, CivilReadRequest request, View3D geometryView,
            CivilWallRuleTemplate template, CivilWallReadControl control)
        {
            template = CivilWallRuleTemplates.Clone(template ?? CivilWallRuleTemplates.Default());
            if (ui == null || ui.Document == null || !ui.Document.IsValidObject || ui.Document.IsFamilyDocument) throw new InvalidOperationException("请先打开有效工程。");
            if (nativeSnapshot == null || nativeSnapshot.Elements == null || request == null) throw new InvalidDataException("交接读取缺少已确认的原生范围。");
            Document document = ui.Document;
            VerifySource(document, nativeSnapshot);
            if (request.DesignOptionId != "-1") throw new InvalidOperationException("交接首批只支持当前根文档主模型。");
            Phase phase = document.GetElement(ParseId(request.PhaseId)) as Phase;
            if (phase == null) throw new InvalidOperationException("交接读取需要当前工程的明确阶段。");
            if (geometryView == null || !geometryView.IsValidObject || geometryView.IsTemplate || !geometryView.Document.Equals(document) ||
                !FilteredElementCollector.IsViewValidForElementIteration(document, geometryView.Id)) throw new InvalidOperationException("请选择本工程的非模板三维几何校验视图。");
            View3D currentView = document.GetElement(geometryView.UniqueId) as View3D;
            if (currentView == null || !currentView.Id.Equals(geometryView.Id)) throw new InvalidOperationException("几何校验视图已失效。");
            Parameter viewPhase = currentView.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (viewPhase == null || !viewPhase.HasValue || viewPhase.StorageType != StorageType.ElementId || Id(viewPhase.AsElementId()) != request.PhaseId)
                throw new InvalidOperationException("三维几何校验视图与交接阶段不一致。");
            var requested = new Dictionary<string,CivilElementRecord>(StringComparer.Ordinal);
            var requestedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilElementRecord source in nativeSnapshot.Elements.Where(item => item != null && item.KindCode == "wall"))
            {
                if (String.IsNullOrWhiteSpace(source.UniqueId) || String.IsNullOrWhiteSpace(source.ElementId) || requested.ContainsKey(source.UniqueId) || !requestedIds.Add(source.ElementId))
                    throw new InvalidDataException("请求墙体身份缺失或重复。");
                requested.Add(source.UniqueId, source);
            }
            if (requested.Count == 0) throw new InvalidOperationException("当前原生范围没有墙体。");
            var bundle = new RevitWallJoinReadBundle { NativeSnapshot = nativeSnapshot, GeometryViewUniqueId = currentView.UniqueId, GeometryViewName = currentView.Name,
                GeometryPhaseId = request.PhaseId, RuleTemplate = template, Operations = new NativeWallJoinSolidOps() };
            var state = new ReadState { Document = document, Phase = phase, View = currentView, Template = template, Control = control, Bundle = bundle, Requested = requested };
            try
            {
                state.Catalog = BuildCatalog(state);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (string root in requested.Keys.OrderBy(uid => uid,StringComparer.Ordinal))
                {
                    if (visited.Contains(root)) continue;
                    Check(state,"读取完整墙体交接组件",visited.Count,requested.Count);
                    if (!state.Catalog.ContainsKey(root)) throw new InvalidOperationException("请求墙体不在完整同阶段主模型目录中：" + root);
                    var component = new List<Prepared>(); var pending = new Queue<string>(); var queued = new HashSet<string>(StringComparer.Ordinal);
                    pending.Enqueue(root); queued.Add(root);
                    while (pending.Count > 0)
                    {
                        string uid = pending.Dequeue(); Prepared item = Prepare(state,uid); component.Add(item);
                        if (component.Count > MaximumComponentWalls) throw new InvalidOperationException("完整相交／连接组件超过 64 面墙，未截断依赖后出量。");
                        ReadRelated(state,item);
                        foreach (string related in item.Related.OrderBy(value => value,StringComparer.Ordinal)) if (queued.Add(related)) pending.Enqueue(related);
                    }
                    foreach (Prepared item in component) visited.Add(item.Entry.Uid);
                    ValidateWallShapes(state,component);
                    bundle.Components.Add(CompleteComponent(state,component,nativeSnapshot,request));
                }
                bundle.CatalogWallCount=state.Catalog.Count;
                bundle.ExcludedUnknownGrossEnvelopeCount=state.Catalog.Values.Count(entry=>entry.CatalogIssue!=null&&!visited.Contains(entry.Uid));
                bundle.Notices.Add("依赖目录包含 "+bundle.CatalogWallCount+" 面同阶段主模型墙；已读取全部显式连接及端点关联，包围盒仅生成候选，原生实体并集交集决定组件。");
                bundle.Notices.Add("独立毛基准未知且不接触已准备组件的原生边界、无显式关联的范围外墙仅排除，不作为已计量毛量；本次数量："+bundle.ExcludedUnknownGrossEnvelopeCount+"。");
                bundle.Notices.Add("不能读取原生边界、阶段或显式依赖时不按不相交排除；当前不支持墙梁柱板交接、链接、L端延伸及未证明轮廓。");
                ReleaseTransientOperands(bundle);
                Check(state,"完成交接依赖读取",bundle.Components.Count,bundle.Components.Count);
                VerifySource(document,nativeSnapshot);
                return bundle;
            }
            catch
            {
                bundle.Dispose(); throw;
            }
        }

        private static Dictionary<string,Entry> BuildCatalog(ReadState state)
        {
            List<Wall> walls;
            using (var collector = new FilteredElementCollector(state.Document).OfClass(typeof(Wall))) walls = collector.Cast<Wall>().Take(MaximumCatalogWalls + 1).ToList();
            if (walls.Count > MaximumCatalogWalls) throw new InvalidOperationException("根文档墙目录超过 20,000 面，无法证明交接依赖完整。");
            var result = new Dictionary<string,Entry>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Wall wall in walls)
            {
                if (result.Count % 32 == 0) Check(state,"读取墙体交接候选目录",result.Count,walls.Count);
                if (wall == null || !wall.IsValidObject || !wall.Document.Equals(state.Document)) throw new InvalidOperationException("墙目录含失效或外部文档对象。");
                if (wall.DesignOption != null) continue;
                ElementOnPhaseStatus phase = wall.GetPhaseStatus(state.Phase.Id);
                if (phase != ElementOnPhaseStatus.New && phase != ElementOnPhaseStatus.Existing) continue;
                string uid = wall.UniqueId, id = Id(wall.Id);
                if (String.IsNullOrWhiteSpace(uid) || result.ContainsKey(uid) || !ids.Add(id)) throw new InvalidOperationException("同阶段墙目录身份不唯一。");
                Bounds actual = ReadBounds(wall);
                // This is a conservative candidate envelope only. Neither the
                // envelope nor a native parameter volume becomes a deduction.
                var entry = new Entry { Wall = wall, Id = id, Uid = uid, ActualBounds = actual, CandidateBounds = actual };
                try { entry.CandidateBounds = Bounds.Join(actual,ParameterEnvelope(wall,actual)); }
                catch (Exception error)
                {
                    if (IsCancellation(error) || error is OutOfMemoryException) throw;
                    entry.CatalogIssue = "独立矩形候选边界不可证明：" + error.GetType().Name;
                }
                result.Add(uid,entry);
            }
            // Read every eligible wall's explicit edges, including incoming
            // one-sided endpoint relations. A far, unsupported wall must not
            // escape a component merely because its native bbox was trimmed.
            foreach(Entry entry in result.Values)
            {
                Check(state,"读取完整墙体显式连接目录",0,result.Count);
                Wall wall=entry.Wall;var linkedIds=new HashSet<string>(StringComparer.Ordinal);
                foreach(ElementId id in JoinGeometryUtils.GetJoinedElements(state.Document,wall))if(!id.Equals(wall.Id)){linkedIds.Add(Id(id));entry.JoinedIds.Add(Id(id));}
                LocationCurve location=wall.Location as LocationCurve;
                if(location!=null)for(int end=0;end<2;end++)foreach(Element joined in location.get_ElementsAtJoin(end))if(joined!=null&&!joined.Id.Equals(wall.Id)){linkedIds.Add(Id(joined.Id));entry.EndpointIds.Add(Id(joined.Id));}
                foreach(string id in linkedIds)
                {
                    Element element=state.Document.GetElement(ParseId(id));Wall other=element as Wall;Entry target;
                    if(other==null||!other.IsValidObject||!other.Document.Equals(state.Document)||!result.TryGetValue(other.UniqueId,out target))
                    {entry.JoinIssue="显式连接含未支持类别、外部或不属于已确认阶段的对象，ElementId="+id;continue;}
                    entry.Explicit.Add(target.Uid);target.Explicit.Add(entry.Uid);
                }
            }
            return result;
        }

        private static Bounds ParameterEnvelope(Wall wall,Bounds actual)
        {
            LocationCurve location = wall.Location as LocationCurve; Line line = location == null ? null : location.Curve as Line;
            if (line == null || !line.IsBound || Math.Abs(line.Direction.Z) > 1e-9 || !Finite(line.Length) || line.Length <= 0 || wall.WallType == null)
                throw new InvalidOperationException("没有可构造的水平直线参数边界。");
            double height = RequiredLength(wall,BuiltInParameter.WALL_USER_HEIGHT_PARAM).Value.Value / M;
            double offset = RequiredLength(wall,BuiltInParameter.WALL_BASE_OFFSET).Value.Value / M;
            Parameter baseParameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Level level = baseParameter != null && baseParameter.HasValue && baseParameter.StorageType == StorageType.ElementId ? wall.Document.GetElement(baseParameter.AsElementId()) as Level : null;
            if (level == null || !Finite(level.ProjectElevation)) throw new InvalidOperationException("基底楼层不可读。");
            double width = wall.WallType.Width; if (!Finite(width) || width <= 0 || height <= 0) throw new InvalidOperationException("参数边界尺寸无效。");
            XYZ origin = line.GetEndPoint(0), axis = line.Direction, normal = new XYZ(-axis.Y,axis.X,0);
            double n0 = Double.PositiveInfinity, n1 = Double.NegativeInfinity;
            foreach (XYZ point in Corners(actual)) { double n = (point-origin).DotProduct(normal); n0=Math.Min(n0,n); n1=Math.Max(n1,n); }
            // Any supported transverse plane inferred from native edge extrema
            // lies inside the full model bbox projection; padding width makes
            // this envelope conservative even before native shape preparation.
            return FrameBounds(origin,axis,normal,line.Length,n0-width,n1+width,level.ProjectElevation+offset,height);
        }

        private static Prepared Prepare(ReadState state,string uid)
        {
            Prepared cached; if (state.Cache.TryGetValue(uid,out cached)) return cached;
            Entry entry = state.Catalog[uid]; CivilElementRecord source; state.Requested.TryGetValue(uid,out source);
            Wall wall = entry.Wall;
            var fact = new CivilWallFact { ElementId=entry.Id,UniqueId=uid,Name=wall.Name,
                NativeLength=source==null?Metric(wall,BuiltInParameter.CURVE_ELEM_LENGTH,"m",M):source.Length,
                NativeArea=source==null?Metric(wall,BuiltInParameter.HOST_AREA_COMPUTED,"m2",M*M):source.Area,
                NativeVolume=source==null?Metric(wall,BuiltInParameter.HOST_VOLUME_COMPUTED,"m3",M3):source.Volume };
            var prepared = new Prepared { Entry=entry, Evidence=new CivilWallJoinWallEvidence { Fact=fact,ActualBoundsFeet=entry.ActualBounds.Values(),ExplicitWallJoinUniqueIds=new List<string>() },
                Node=new CivilWallJoinNode { UniqueId=uid,ElementId=entry.Id,Requested=source!=null,Supported=false }, Issue=entry.CatalogIssue };
            state.Cache.Add(uid,prepared);
            try
            {
                Check(state,"准备墙体交接独立基准",state.Cache.Count,state.Catalog.Count);
                if (source != null && (!source.Supported || source.ElementId != entry.Id)) throw new Unsupported("原始范围墙身份／业务类别未获确认。");
                RequireCurrentIdentity(state,entry);
                ReadExplicitJoins(state,prepared);
                if (prepared.Issue != null) throw new Unsupported(prepared.Issue);
                WallType type = wall.WallType;
                if (type == null || type.Kind != WallKind.Basic || wall.IsStackedWall || wall.IsStackedWallMember) throw new Unsupported("交接首批只支持非叠层的普通基本墙。");
                fact.TypeUniqueId=type.UniqueId;fact.TypeName=type.Name;
                fact.ParameterLength=RequiredLength(wall,BuiltInParameter.CURVE_ELEM_LENGTH);fact.ParameterHeight=RequiredLength(wall,BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                fact.ParameterWidth=Scalar(type.Width,M,"m","WallType.Width");
                fact.AttachedTop=Attached(wall,BuiltInParameter.WALL_TOP_IS_ATTACHED);fact.AttachedBottom=Attached(wall,BuiltInParameter.WALL_BOTTOM_IS_ATTACHED);
                Parameter top=wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);fact.TopConstraintId=top!=null&&top.HasValue&&top.StorageType==StorageType.ElementId?Id(top.AsElementId()):String.Empty;
                if (fact.AttachedTop!=false || fact.AttachedBottom!=false || fact.TopConstraintId!="-1") throw new Unsupported("附着或顶部约束墙不能使用当前独立矩形基准。");
                double length=fact.ParameterLength.Value.Value/M,height=fact.ParameterHeight.Value.Value/M,width=fact.ParameterWidth.Value.Value/M;
                if (!Finite(width)||width<=0) throw new Unsupported("墙类型厚度无效。");
                Parameter baseParameter=wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                Level level=baseParameter!=null&&baseParameter.HasValue&&baseParameter.StorageType==StorageType.ElementId?state.Document.GetElement(baseParameter.AsElementId())as Level:null;
                CivilMetric offset=RequiredLength(wall,BuiltInParameter.WALL_BASE_OFFSET);
                if(level==null||!Finite(level.ProjectElevation))throw new Unsupported("基底楼层不可读。");
                double baseZ=level.ProjectElevation+offset.Value.Value/M;
                fact.BaseElevationMetres=baseZ*M;fact.BaseElevationSource="Level.ProjectElevation + WALL_BASE_OFFSET";
                LocationCurve location=wall.Location as LocationCurve;Line line=location==null?null:location.Curve as Line;
                fact.CurveKind=location==null||location.Curve==null?"missing":location.Curve.GetType().Name;
                if(line==null||!line.IsBound||Math.Abs(line.Direction.Z)>1e-9||!Finite(line.Length)||line.Length<=0||Math.Abs(line.Length-length)>CoordinateToleranceFeet)
                    throw new Unsupported("墙轴不是可核对实例长度的水平有界直线。");
                XYZ origin=line.GetEndPoint(0),axis=line.Direction,normal=new XYZ(-axis.Y,axis.X,0);
                prepared.Evidence.OriginFeet=Coordinates(origin);prepared.Evidence.Axis=Coordinates(axis);prepared.Evidence.Normal=Coordinates(normal);
                if(!RevitCivilWallReader.ReadConfirmedInserts(state.Document,wall,state.Phase,origin,axis,normal,baseZ,fact,state.Template,state.Control))throw new Unsupported("宿主切洞读取未完成。");
                if(fact.Inserts.Any(item=>item==null||item.Status=="unsupported"))throw new Unsupported("存在未证明的宿主切洞，未用相邻墙并集掩盖未知 void。");
                var confirmed=new HashSet<string>(fact.Inserts.Where(item=>item.Status=="recognized").Select(item=>item.ElementId),StringComparer.Ordinal);
                foreach(ElementId id in InstanceVoidCutUtils.GetCuttingVoidInstances(wall))
                    if(!confirmed.Contains(Id(id)))throw new Unsupported("存在未纳入已确认宿主切洞的独立空心族，ElementId="+Id(id));
                double n0,n1;
                prepared.Actual=ReadActual(state,wall,origin,normal,out n0,out n1);
                if(Math.Abs(n1-n0-width)>CoordinateToleranceFeet)throw new Unsupported("实际实体横向范围与固定墙厚不一致。");
                prepared.Evidence.NormalMinFeet=n0;
                prepared.Evidence.GrossBoundsFeet=FrameBounds(origin,axis,normal,length,n0,n0+width,baseZ,height).Values();
                prepared.Expected=Prism(state,origin,axis,normal,0,length,n0,n0+width,baseZ,height);
                double gross=Volume(prepared.Expected);prepared.Node.GrossVolumeMetres3=gross;
                double dimensional=length*height*width*M3;
                prepared.Tolerance=Math.Max(state.Template.AbsoluteToleranceMetres3,gross*state.Template.RelativeTolerance);
                if(!Finite(gross)||gross<=0||Math.Abs(gross-dimensional)>prepared.Tolerance)throw new Unsupported("原生基准棱柱与独立长高厚体积不一致。");
                foreach(CivilWallInsert insert in fact.Inserts.Where(item=>item.Status=="recognized"))
                {
                    Check(state,"构造已证明的矩形切洞",0,fact.Inserts.Count);
                    if(insert.HostUniqueId!=uid||insert.Kind!="rect-opening"||!ValidCoordinate(insert.RectX0)||!ValidCoordinate(insert.RectX1)||!ValidCoordinate(insert.RectZ0)||!ValidCoordinate(insert.RectZ1))throw new Unsupported("已确认矩形切洞事实身份或坐标失效。");
                    double x0=insert.RectX0.Value/M,x1=insert.RectX1.Value/M,z0=insert.RectZ0.Value/M,z1=insert.RectZ1.Value/M;
                    if(x1<=x0||z1<=z0)throw new Unsupported("矩形切洞退化。");
                    Solid cutter=Prism(state,origin,axis,normal,x0,x1,n0-CoordinateToleranceFeet*10,n1+CoordinateToleranceFeet*10,baseZ+z0,z1-z0);
                    prepared.Expected=Boolean(state,prepared.Expected,cutter,BooleanOperationsType.Difference);
                }
                double expected=Volume(prepared.Expected),actual=Volume(prepared.Actual);
                if(expected>gross+prepared.Tolerance)throw new Unsupported("扣洞后的基准实体反而增加体积。");
                prepared.Evidence.ExpectedVolumeMetres3=expected;prepared.Evidence.ActualVolumeMetres3=actual;
                fact.GeometryVolumeMetres3=actual;fact.GeometryToleranceMetres3=prepared.Tolerance;
                prepared.Node.ExpectedSolid=prepared.Expected;prepared.Node.ActualSolid=prepared.Actual;
                prepared.Reach=Boolean(state,prepared.Expected,prepared.Actual,BooleanOperationsType.Union);
                prepared.Node.Supported=true;prepared.Node.Issue=String.Empty;
            }
            catch(Exception error)
            {
                error=Unwrap(error);if(IsCancellation(error)||error is OutOfMemoryException)throw;
                prepared.Issue=(prepared.Issue==null?String.Empty:prepared.Issue+"；")+error.Message;
                fact.Status=error is Unsupported?"unsupported":"failed";fact.Issues.Add(prepared.Issue);prepared.Node.Supported=false;prepared.Node.Issue=prepared.Issue;
            }
            return prepared;
        }

        private static void ReadExplicitJoins(ReadState state,Prepared prepared)
        {
            prepared.Evidence.Fact.JoinedElementIds.AddRange(prepared.Entry.JoinedIds);
            prepared.Evidence.Fact.EndpointJoinedElementIds.AddRange(prepared.Entry.EndpointIds);
            if(prepared.Entry.JoinIssue!=null)prepared.Issue=prepared.Entry.JoinIssue;
            foreach(string uid in prepared.Entry.Explicit){prepared.Explicit.Add(uid);prepared.Related.Add(uid);}
            prepared.Evidence.ExplicitWallJoinUniqueIds=prepared.Explicit.OrderBy(value=>value,StringComparer.Ordinal).ToList();
        }

        private static void ReadRelated(ReadState state,Prepared source)
        {
            List<Entry> candidates=state.Catalog.Values.Where(entry=>entry.Uid!=source.Entry.Uid&&entry.CandidateBounds.MayOverlap(source.Entry.CandidateBounds)).OrderBy(entry=>entry.Uid,StringComparer.Ordinal).ToList();
            source.Evidence.Fact.OverlapCandidateCount=candidates.Count;
            if(candidates.Count>MaximumCandidates)throw new InvalidOperationException("单墙交接候选超过 64 面，未截断后宣称依赖完整。");
            foreach(Entry entry in candidates)
            {
                Check(state,"核对毛基准与实际实体依赖",0,candidates.Count);
                Prepared candidate=Prepare(state,entry.Uid);
                bool related=source.Explicit.Contains(entry.Uid)||candidate.Explicit.Contains(source.Entry.Uid);
                if(!related)
                {
                    // Unsupported candidates cannot prove absence of overlap;
                    // conservatively retain their potential dependency.
                    if(!source.Node.Supported||!candidate.Node.Supported||source.Reach==null||candidate.Reach==null)related=true;
                    else
                    {
                        Solid intersection=Boolean(state,source.Reach,candidate.Reach,BooleanOperationsType.Intersect);
                        related=Volume(intersection)>0;
                    }
                }
                if(related){source.Related.Add(entry.Uid);candidate.Related.Add(source.Entry.Uid);}
            }
            source.Evidence.Fact.InterwallOverlapCheckComplete=true;
        }

        private static void ValidateWallShapes(ReadState state,List<Prepared> component)
        {
            var byUid=component.ToDictionary(item=>item.Entry.Uid,StringComparer.Ordinal);
            foreach(Prepared item in component)
            {
                if(!item.Node.Supported)continue;
                try
                {
                    Solid extra=Boolean(state,item.Actual,item.Expected,BooleanOperationsType.Difference);
                    Solid missing=Boolean(state,item.Expected,item.Actual,BooleanOperationsType.Difference);
                    double extraVolume=Volume(extra),missingVolume=Volume(missing);
                    item.Evidence.Fact.SymmetricDifferenceMetres3=extraVolume+missingVolume;
                    if(extraVolume>item.Tolerance)throw new Unsupported("实际墙超出独立洞后基准；端部延伸或新增形状尚未支持。");
                    if(missingVolume>item.Tolerance)
                    {
                        if(item.Explicit.Count==0)throw new Unsupported("未连接墙自身形状与洞后基准不一致，不能用邻墙并集掩盖未知轮廓或切割。");
                        if(!ProveNoProfileSketch(state,item.Entry.Wall))throw new Unsupported("连接墙缺少可验证的无编辑轮廓证据，未把未知轮廓裁切当作交接。");
                        Solid remainder=Clone(state,missing);
                        foreach(string uid in item.Explicit.OrderBy(value=>value,StringComparer.Ordinal))
                        {
                            Prepared neighbor;
                            if(!byUid.TryGetValue(uid,out neighbor)||!neighbor.Node.Supported||neighbor.Expected==null)throw new Unsupported("显式连接的完整洞后邻墙基准不可用。");
                            remainder=Boolean(state,remainder,neighbor.Expected,BooleanOperationsType.Difference);
                        }
                        if(Volume(remainder)>item.Tolerance)throw new Unsupported("墙体缺失形状超出全部显式连接邻墙的洞后基准，无法证明仅为交接影响。");
                    }
                    item.Evidence.Fact.GeometryVerified=false;item.Evidence.Fact.Status="component-prepared";
                }
                catch(Exception error)
                {
                    error=Unwrap(error);if(IsCancellation(error)||error is OutOfMemoryException)throw;
                    item.Node.Supported=false;item.Issue=error.Message;item.Node.Issue=item.Issue;item.Evidence.Issue=item.Issue;
                    item.Evidence.Fact.GeometryVerified=false;item.Evidence.Fact.Status=error is Unsupported?"unsupported":"failed";item.Evidence.Fact.Issues.Add(item.Issue);
                }
            }
        }

        private static bool ProveNoProfileSketch(ReadState state,Wall wall)
        {
            PropertyInfo sketch=typeof(Wall).GetProperty("SketchId",BindingFlags.Instance|BindingFlags.Public);
            if(sketch!=null&&sketch.PropertyType==typeof(ElementId))
            {ElementId id=sketch.GetValue(wall,null)as ElementId;return id!=null&&Id(id)=="-1";}
            MethodInfo dependents=typeof(Element).GetMethod("GetDependentElements",BindingFlags.Instance|BindingFlags.Public,null,new[]{typeof(ElementFilter)},null);
            if(dependents==null)return false;
            IEnumerable values=dependents.Invoke(wall,new object[]{null})as IEnumerable;
            if(values==null)return false;int count=0;
            foreach(object value in values)
            {
                if(++count>4096)return false;Check(state,"核对连接墙轮廓来源",count,0);
                ElementId id=value as ElementId;if(id==null)return false;Element element=state.Document.GetElement(id);
                if(element==null||!element.IsValidObject||element is Sketch)return false;
            }
            return true;
        }

        private static RevitWallJoinPreparedComponent CompleteComponent(ReadState state,List<Prepared> component,CivilSnapshot native,CivilReadRequest request)
        {
            component=component.OrderBy(item=>item.Entry.Uid,StringComparer.Ordinal).ToList();
            foreach(Prepared item in component)item.Evidence.Issue=item.Issue??String.Empty;
            string fingerprint=DependencyFingerprint(state,component,native,request);
            bool supported=component.All(item=>item.Node.Supported);
            string message=supported?"完整墙组件已准备；请明确全部参与墙的优先顺序，再证明并集并分配体积。":String.Join("；",component.Where(item=>!item.Node.Supported).Select(item=>item.Entry.Id+"："+item.Issue));
            var input=new CivilWallJoinInput { ContextKey=fingerprint,DependenciesComplete=true,AbsoluteToleranceMetres3=state.Template.AbsoluteToleranceMetres3,
                RelativeTolerance=state.Template.RelativeTolerance,MaximumOperations=MaximumNativeOperations,Nodes=component.Select(item=>item.Node).ToList() };
            return new RevitWallJoinPreparedComponent { ComponentId=fingerprint,Binding=new CivilWallJoinBinding { DocumentScope=native.DocumentScope,SourcePath=native.SourcePath??String.Empty,
                PhaseId=request.PhaseId,GeometryViewUniqueId=state.View.UniqueId,SourceRevision=native.Revision,GeometryDependencyFingerprint=fingerprint },
                Evidence=component.Select(item=>item.Evidence).ToList(),Input=input,Status=supported?"ready":"unsupported",Message=message,
                Participants=component.Select(item=>new CivilWallJoinParticipant { ElementUniqueId=item.Entry.Uid,ElementId=item.Entry.Id,Name=item.Entry.Wall.Name,Requested=item.Node.Requested,
                    GrossVolumeMetres3=item.Expected==null?(double?)null:item.Node.GrossVolumeMetres3,
                    OpeningVolumeMetres3=item.Evidence.ExpectedVolumeMetres3.HasValue?Math.Max(0,item.Node.GrossVolumeMetres3-item.Evidence.ExpectedVolumeMetres3.Value):(double?)null,
                    NativeVolumeMetres3=NativeValue(item.Evidence.Fact.NativeVolume,"m3") }).ToList() };
        }

        // Requested selection, capture time, report scope and volatile
        // in-process revision counter are deliberately excluded. A reopened
        // unchanged component can explicitly adopt its old priority plan;
        // Binding.SourceRevision still guards each live read/calculation.
        private static string DependencyFingerprint(ReadState state,List<Prepared> component,CivilSnapshot native,CivilReadRequest request)
        {
            var nodes=new List<object>();
            foreach(Prepared item in component)
            {
                CivilWallFact fact=item.Evidence.Fact;
                nodes.Add(new Dictionary<string,object> {
                    {"uid",item.Entry.Uid},{"id",item.Entry.Id},{"name",fact.Name},{"typeUid",fact.TypeUniqueId},{"typeName",fact.TypeName},{"status",fact.Status},{"issue",item.Issue},
                    {"length",MetricProof(fact.ParameterLength)},{"height",MetricProof(fact.ParameterHeight)},{"width",MetricProof(fact.ParameterWidth)},
                    {"baseZ",Bits(fact.BaseElevationMetres)},{"topConstraint",fact.TopConstraintId},{"attachedTop",fact.AttachedTop},{"attachedBottom",fact.AttachedBottom},
                    {"origin",Bits(item.Evidence.OriginFeet)},{"axis",Bits(item.Evidence.Axis)},{"normal",Bits(item.Evidence.Normal)},{"normalMin",Bits(item.Evidence.NormalMinFeet)},
                    {"grossBounds",Bits(item.Evidence.GrossBoundsFeet)},{"actualBounds",Bits(item.Evidence.ActualBoundsFeet)},
                    {"grossVolume",Bits(item.Node.GrossVolumeMetres3)},{"expectedVolume",Bits(item.Evidence.ExpectedVolumeMetres3)},{"actualVolume",Bits(item.Evidence.ActualVolumeMetres3)},
                    {"symmetricDifference",Bits(fact.SymmetricDifferenceMetres3)},{"shapeTolerance",Bits(fact.GeometryToleranceMetres3)},
                    {"explicit",item.Explicit.OrderBy(value=>value,StringComparer.Ordinal).ToArray()},{"related",item.Related.Where(uid=>component.Any(node=>node.Entry.Uid==uid)).OrderBy(value=>value,StringComparer.Ordinal).ToArray()},
                    {"inserts",fact.Inserts.OrderBy(insert=>insert.UniqueId,StringComparer.Ordinal).Select(insert=>(object)new Dictionary<string,object> {
                        {"uid",insert.UniqueId},{"id",insert.ElementId},{"host",insert.HostUniqueId},{"kind",insert.Kind},{"method",insert.CutoutMethod},{"status",insert.Status},{"source",insert.Source},
                        {"x0",Bits(insert.RectX0)},{"x1",Bits(insert.RectX1)},{"z0",Bits(insert.RectZ0)},{"z1",Bits(insert.RectZ1)},{"issues",insert.Issues.ToArray()} }).ToArray()}
                });
            }
            return PackageWriter.Hash(Json.Encode(new Dictionary<string,object> { {"contract","BV.Wall.JoinDependencies/1"},{"document",native.DocumentScope},
                {"sourcePath",String.IsNullOrEmpty(native.SourcePath)?String.Empty:Path.GetFullPath(native.SourcePath).ToLowerInvariant()},
                {"phase",request.PhaseId},{"view",state.View.UniqueId},{"rule",CivilWallRuleTemplates.Fingerprint(state.Template)},{"nodes",nodes} }));
        }
        private static object MetricProof(CivilMetric metric)
        {return metric==null?null:new object[]{metric.Status,metric.Unit,metric.Source,Bits(metric.Value)};}
        private static string Bits(double? value){return value.HasValue?BitConverter.DoubleToInt64Bits(value.Value).ToString("x16",CultureInfo.InvariantCulture):null;}
        private static string[] Bits(double[] values){return values==null?null:values.Select(value=>Bits((double?)value)).ToArray();}

        private static Solid ReadActual(ReadState state,Wall wall,XYZ origin,XYZ normal,out double minimumN,out double maximumN)
        {
            minimumN=Double.PositiveInfinity;maximumN=Double.NegativeInfinity;
            using(var options=new Options())
            {
                options.View=state.View;options.ComputeReferences=false;options.IncludeNonVisibleObjects=false;
                using(GeometryElement geometry=wall.get_Geometry(options))
                {
                    if(geometry==null)throw new Unsupported("三维校验上下文未提供墙实体。");
                    Solid actual=null;
                    foreach(GeometryObject value in geometry)
                    {
                        if(value is Mesh||value is GeometryInstance)throw new Unsupported("墙几何含未支持的网格或实例分支。");
                        Solid solid=value as Solid;if(solid==null)continue;
                        double volume=Volume(solid);if(solid.Faces.Size==0&&volume>0)throw new Unsupported("无面的分支实体却报告正体积。");if(volume<=0)continue;
                        Solid cloned=Clone(state,solid);actual=actual==null?cloned:Boolean(state,actual,cloned,BooleanOperationsType.Union);
                        foreach(Edge edge in solid.Edges)
                        {
                            Curve curve=edge.AsCurve();
                            // Conservative first scope: straight native edges.
                            // Curved/elliptic geometry is never approximated by
                            // tessellation when locating the gross side planes.
                            Line line=curve as Line;if(line==null)throw new Unsupported("墙实体含曲线边，当前横向基准未采用采样极值猜测。");
                            foreach(XYZ point in new[]{line.GetEndPoint(0),line.GetEndPoint(1)})
                            {double n=(point-origin).DotProduct(normal);if(!Finite(n))throw new Unsupported("原生实体横向坐标无效。");minimumN=Math.Min(minimumN,n);maximumN=Math.Max(maximumN,n);}
                        }
                    }
                    if(actual==null||!Finite(minimumN)||!Finite(maximumN))throw new Unsupported("没有可独立保留的正体积墙实体与明确横向边界。");
                    return actual;
                }
            }
        }
        private static Solid Clone(ReadState state,Solid solid)
        {OperationBefore(state);Solid result=CopySolid(solid);if(Object.ReferenceEquals(result,solid))throw new InvalidOperationException("原生Clone返回借用输入别名。");result=Own(state,result);OperationAfter(state);return result;}
        internal static Solid CopySolid(Solid solid)
        {
#if REVIT_2015
            // The documented pre-2016 SplitVolumes contract returns a copy
            // when no split is required. Multiple-volume legacy operands are
            // refused, not silently reduced to one of their parts.
            IList<Solid> parts=SolidUtils.SplitVolumes(solid);
            if(parts!=null&&parts.Count==1&&parts[0]!=null&&!Object.ReferenceEquals(parts[0],solid))return parts[0];
            if(parts!=null){var disposed=new List<Solid>();foreach(Solid part in parts)if(part!=null&&!Object.ReferenceEquals(part,solid)&&!disposed.Any(value=>Object.ReferenceEquals(value,part))){disposed.Add(part);part.Dispose();}}
            throw new InvalidOperationException("旧宿主没有提供可独立保留的单体积复制实体。");
#else
            return SolidUtils.Clone(solid);
#endif
        }
        private static Solid Boolean(ReadState state,Solid left,Solid right,BooleanOperationsType operation)
        {OperationBefore(state);Solid result=BooleanOperationsUtils.ExecuteBooleanOperation(left,right,operation);if(Object.ReferenceEquals(result,left)||Object.ReferenceEquals(result,right))throw new InvalidOperationException("原生布尔操作返回借用输入别名。");result=Own(state,result);OperationAfter(state);return result;}
        private static Solid Prism(ReadState state,XYZ origin,XYZ axis,XYZ normal,double x0,double x1,double n0,double n1,double baseZ,double height)
        {OperationBefore(state);Solid result=Own(state,RevitCivilWallReader.CreateParameterPrism(origin,axis,normal,x0,x1,n0,n1,baseZ,height));OperationAfter(state);return result;}
        private static Solid Own(ReadState state,Solid solid)
        {if(solid==null)throw new InvalidOperationException("原生实体创建失败。");if(state.Bundle.Owned.Any(value=>Object.ReferenceEquals(value,solid)))throw new InvalidOperationException("原生实体所有权重复。");state.Bundle.Owned.Add(solid);return solid;}
        private static void ReleaseTransientOperands(RevitWallJoinReadBundle bundle)
        {
            var keep=new List<object>();
            foreach(CivilWallJoinNode node in bundle.Components.SelectMany(component=>component.Input.Nodes)){if(node.ExpectedSolid!=null)keep.Add(node.ExpectedSolid);if(node.ActualSolid!=null)keep.Add(node.ActualSolid);}
            for(int index=bundle.Owned.Count-1;index>=0;index--)if(!keep.Any(value=>Object.ReferenceEquals(value,bundle.Owned[index])))
            {Solid transient=bundle.Owned[index];transient.Dispose();bundle.Owned.RemoveAt(index);}
        }
        private static void OperationBefore(ReadState state){Check(state,"交接原生实体操作前",state.NativeOperations,MaximumNativeOperations);if(++state.NativeOperations>MaximumNativeOperations)throw new InvalidOperationException("交接准备超过 20,000 次原生实体操作，未交付半组件。");}
        private static void OperationAfter(ReadState state){Check(state,"交接原生实体操作后",state.NativeOperations,MaximumNativeOperations);}
        internal static double Volume(Solid solid){double value=solid.Volume*M3;if(!Finite(value)||value<0)throw new InvalidOperationException("原生实体体积无效。");return value;}
        private static void Check(ReadState state,string stage,int done,int total){if(state.Control!=null)state.Control.Checkpoint(stage,done,total);}
        private static void VerifySource(Document document,CivilSnapshot native)
        {string identity=document.ProjectInformation==null?document.Title:document.ProjectInformation.UniqueId;if(!document.IsValidObject||PackageWriter.Hash(identity).Substring(0,24)!=native.DocumentScope||!String.Equals(document.PathName??String.Empty,native.SourcePath??String.Empty,StringComparison.OrdinalIgnoreCase)||RevitCivilQuantityReader.Revision(document)!=native.Revision)throw new InvalidOperationException("交接读取期间来源文档或修订变化，未提交旧数据。");}
        private static void RequireCurrentIdentity(ReadState state,Entry entry)
        {Element current=state.Document.GetElement(entry.Uid);if(current==null||!current.IsValidObject||!(current is Wall)||Id(current.Id)!=entry.Id||!current.Document.Equals(state.Document))throw new Unsupported("墙体身份在目录与实体准备之间发生变化。");}
        private static Bounds ReadBounds(Wall wall)
        {
            using(BoundingBoxXYZ box=wall.get_BoundingBox(null))
            {
                if(box==null||box.Min==null||box.Max==null||box.Transform==null||box.Min.X>box.Max.X||box.Min.Y>box.Max.Y||box.Min.Z>box.Max.Z)throw new InvalidOperationException("同阶段墙完整模型包围盒不可读，不能据此排除依赖。");
                return PointBounds(new[]{box.Min.X,box.Max.X}.SelectMany(x=>new[]{box.Min.Y,box.Max.Y}.SelectMany(y=>new[]{box.Min.Z,box.Max.Z}.Select(z=>box.Transform.OfPoint(new XYZ(x,y,z))))));
            }
        }
        private static Bounds FrameBounds(XYZ origin,XYZ axis,XYZ normal,double length,double n0,double n1,double baseZ,double height)
        {return PointBounds(new[]{0.0,length}.SelectMany(x=>new[]{n0,n1}.SelectMany(n=>new[]{baseZ,baseZ+height}.Select(z=>new XYZ(origin.X+axis.X*x+normal.X*n,origin.Y+axis.Y*x+normal.Y*n,z)))));}
        private static Bounds PointBounds(IEnumerable<XYZ> points)
        {var result=new Bounds{X0=Double.PositiveInfinity,X1=Double.NegativeInfinity,Y0=Double.PositiveInfinity,Y1=Double.NegativeInfinity,Z0=Double.PositiveInfinity,Z1=Double.NegativeInfinity};foreach(XYZ point in points){if(point==null||!Finite(point.X)||!Finite(point.Y)||!Finite(point.Z))throw new InvalidOperationException("依赖包围盒坐标无效。");result.X0=Math.Min(result.X0,point.X);result.X1=Math.Max(result.X1,point.X);result.Y0=Math.Min(result.Y0,point.Y);result.Y1=Math.Max(result.Y1,point.Y);result.Z0=Math.Min(result.Z0,point.Z);result.Z1=Math.Max(result.Z1,point.Z);}return result;}
        private static IEnumerable<XYZ> Corners(Bounds bounds){return new[]{bounds.X0,bounds.X1}.SelectMany(x=>new[]{bounds.Y0,bounds.Y1}.SelectMany(y=>new[]{bounds.Z0,bounds.Z1}.Select(z=>new XYZ(x,y,z))));}
        private static CivilMetric RequiredLength(Element element,BuiltInParameter key)
        {CivilMetric value=Metric(element,key,"m",M);if(value.Status!="available"||!value.Value.HasValue||(key!=BuiltInParameter.WALL_BASE_OFFSET&&value.Value.Value<=0))throw new Unsupported("缺少可用的固定实例参数："+key);return value;}
        private static CivilMetric Metric(Element element,BuiltInParameter key,string unit,double factor)
        {Parameter parameter=element.get_Parameter(key);if(parameter==null||!parameter.HasValue)return new CivilMetric{Unit=unit,Source=key.ToString(),Status="missing"};if(parameter.StorageType!=StorageType.Double)return new CivilMetric{Unit=unit,Source=key.ToString(),Status="invalid"};return Scalar(parameter.AsDouble(),factor,unit,key.ToString());}
        private static CivilMetric Scalar(double value,double factor,string unit,string source){double converted=value*factor;return new CivilMetric{Unit=unit,Source=source,Status=Finite(converted)?"available":"invalid",Value=Finite(converted)?(double?)converted:null};}
        private static bool? Attached(Wall wall,BuiltInParameter key){Parameter parameter=wall.get_Parameter(key);if(parameter==null||!parameter.HasValue||parameter.StorageType!=StorageType.Integer)return null;int value=parameter.AsInteger();return value==0?(bool?)false:value==1?(bool?)true:null;}
        private static double? NativeValue(CivilMetric metric,string unit){return metric!=null&&metric.Unit==unit&&metric.Status=="available"&&metric.Value.HasValue&&Finite(metric.Value.Value)?metric.Value:null;}
        private static bool ValidCoordinate(double? value){return value.HasValue&&Finite(value.Value);}
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        private static Exception Unwrap(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;return error;}
        private static bool IsCancellation(Exception error){return error is OperationCanceledException||error.GetType().FullName=="Autodesk.Revit.Exceptions.OperationCanceledException";}
        private static ElementId ParseId(string value)
        {
#if REVIT_2024_OR_GREATER
            long parsed;if(!Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out parsed))throw new InvalidOperationException("元素ID格式无效。");return new ElementId(parsed);
#else
            int parsed;if(!Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out parsed))throw new InvalidOperationException("元素ID格式无效。");return new ElementId(parsed);
#endif
        }
        private static string Id(ElementId value){if(value==null)return"-1";
#if REVIT_2024_OR_GREATER
            return value.Value.ToString(CultureInfo.InvariantCulture);
#else
            return value.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }
        private static double[] Coordinates(XYZ point){return new[]{point.X,point.Y,point.Z};}
        private sealed class Unsupported:Exception{internal Unsupported(string message):base(message){}}
    }
}
