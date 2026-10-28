using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BrightVision.Civil;
using BrightVision.Export;

namespace BrightVision.Revit
{
    // Read-only Revit adapter for the first civil workbench. No transaction,
    // parameter write, geometry mutation, view activation, or source save.
    internal static class RevitCivilQuantityReader
    {
        private const double MetresPerFoot = 0.3048;
        private const double LevelToleranceFeet = 0.001 / MetresPerFoot;
        private static readonly Dictionary<Document, long> Revisions = new Dictionary<Document, long>();
        private static UIControlledApplication attached;

        internal static void Attach(UIControlledApplication application)
        {
            if (application == null) throw new ArgumentNullException("application");
            if (attached != null) return;
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            attached = application;
        }

        internal static void Detach(UIControlledApplication application)
        {
            if (attached != null) attached.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            attached = null;
            Revisions.Clear();
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            Document document = args.GetDocument();
            if (document == null) return;
            long current;
            Revisions.TryGetValue(document, out current);
            Revisions[document] = current == Int64.MaxValue ? Int64.MaxValue : current + 1;
        }

        internal static long Revision(Document document)
        {
            long value;
            return document != null && Revisions.TryGetValue(document, out value) ? value : 0;
        }

        internal static CivilReadRequest DefaultRequest(UIDocument uiDocument)
        {
            Document document = RequireDocument(uiDocument);
            string phaseId = String.Empty;
            View view = uiDocument.ActiveView;
            if (view != null)
            {
                Parameter parameter = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (parameter != null && parameter.HasValue && parameter.StorageType == StorageType.ElementId)
                {
                    ElementId id = parameter.AsElementId();
                    if (document.GetElement(id) is Phase) phaseId = Id(id);
                }
            }
            if (phaseId.Length == 0) foreach (Phase phase in document.Phases) phaseId = Id(phase.Id);
            return new CivilReadRequest { Scope = DefaultViewScope(document, view) ? "view" : "project", PhaseId = phaseId, DesignOptionId = "-1" };
        }

        private static bool DefaultViewScope(Document document, View view)
        {
            if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule) return false;
            try { return FilteredElementCollector.IsViewValidForElementIteration(document, view.Id); }
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                return false;
            }
        }

        internal static IList<CivilChoice> GetPhases(Document document)
        {
            if (document == null || !document.IsValidObject) throw new InvalidOperationException("当前工程已失效。");
            List<CivilChoice> choices = new List<CivilChoice>();
            foreach (Phase phase in document.Phases) choices.Add(new CivilChoice { Id = Id(phase.Id), Label = phase.Name });
            return choices;
        }

        internal static IList<CivilChoice> GetDesignOptions(Document document)
        {
            if (document == null || !document.IsValidObject) throw new InvalidOperationException("当前工程已失效。");
            List<CivilChoice> choices = new List<CivilChoice> { new CivilChoice { Id = "-1", Label = "主模型" } };
            using (FilteredElementCollector collector = new FilteredElementCollector(document).OfClass(typeof(DesignOption)))
                foreach (DesignOption option in collector.Cast<DesignOption>().OrderBy(option => option.Name, StringComparer.Ordinal).ThenBy(option => Id(option.Id), StringComparer.Ordinal))
                    choices.Add(new CivilChoice { Id = Id(option.Id), Label = option.Name + " [" + Id(option.Id) + "] + 主模型" });
            return choices;
        }

        internal static CivilSnapshot Read(UIDocument uiDocument, CivilReadRequest request)
        {
            Document document = RequireDocument(uiDocument);
            if (request == null) throw new ArgumentNullException("request");
            if (request.Scope != "view" && request.Scope != "selection" && request.Scope != "project")
                throw new InvalidOperationException("请选择当前视图、当前选择或当前工程范围。");
            Phase phase = document.GetElement(ParseId(request.PhaseId, false)) as Phase;
            if (phase == null) throw new InvalidOperationException("请选择当前工程中的确定阶段，不能混合不同阶段计算。");
            ElementId optionId = ParseId(request.DesignOptionId, true);
            DesignOption option = optionId.Equals(ElementId.InvalidElementId) ? null : document.GetElement(optionId) as DesignOption;
            if (!optionId.Equals(ElementId.InvalidElementId) && option == null)
                throw new InvalidOperationException("所选设计选项不存在，请重新选择；不能混合不同设计方案。");
            string documentScope = DocumentScope(document);
            CivilSnapshot snapshot = new CivilSnapshot {
                ModelTitle = document.Title, DocumentScope = documentScope, SourcePath = document.PathName ?? String.Empty,
                CapturedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ScopeLabel = request.Scope == "selection" ? "当前选择（根文档）" : request.Scope == "project" ? "当前工程（根文档）" : "当前视图（根文档）",
                PhaseLabel = phase.Name + "（仅现有／新建）",
                DesignOptionLabel = option == null ? "主模型" : option.Name + " [" + Id(option.Id) + "] + 主模型",
                Revision = Revision(document)
            };
            List<Level> levels;
            using (FilteredElementCollector levelCollector = new FilteredElementCollector(document).OfClass(typeof(Level)))
                levels = levelCollector.Cast<Level>().OrderBy(level => level.ProjectElevation).ToList();
            foreach (Level level in levels)
            {
                double elevation = level.ProjectElevation * MetresPerFoot;
                snapshot.Levels.Add(new CivilLevelInfo { Id = Id(level.Id), UniqueId = level.UniqueId, Name = level.Name,
                    ElevationMetres = Double.IsNaN(elevation) || Double.IsInfinity(elevation) ? (double?)null : elevation });
                if (Double.IsNaN(elevation) || Double.IsInfinity(elevation)) snapshot.Notices.Add("原生楼层项目标高无效：" + Id(level.Id));
            }
            CountLinks(document, snapshot);
            snapshot.Notices.Add("原始参数量采用整构件口径，视图裁剪和剖切不会自动裁量；本期不计算清单／定额扣减。");
            snapshot.Notices.Add("仅计量所选阶段的现有／新建构件；拆除、临时、未来、已过阶段及不明阶段均排除。");
            snapshot.Notices.Add("原生楼层保留 Element.LevelId；其无效时仅按类别固定实例参数解析一致的参照层，并保留原始值和来源；冲突不按顺序或几何猜层。");
            snapshot.Notices.Add("楼层标高采用相对项目原点的 ProjectElevation；包围盒跨层仅为候选提示，尚未分层切量。");
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Element element in Candidates(uiDocument, request.Scope))
            {
                if (element == null || element is ElementType) continue;
                string elementId = Id(element.Id);
                if (!seen.Add(elementId)) continue;
                snapshot.CandidateCount++;
                try
                {
                    if (element.DesignOption != null && (option == null || !element.DesignOption.Id.Equals(optionId)))
                    { Exclude(snapshot, element, "不属于已确认的设计选项／主模型范围"); continue; }
                    ElementOnPhaseStatus status = element.GetPhaseStatus(phase.Id);
                    if (status != ElementOnPhaseStatus.Existing && status != ElementOnPhaseStatus.New)
                    { Exclude(snapshot, element, "阶段已排除：" + status.ToString()); continue; }
                    string categoryId = element.Category == null ? String.Empty : Id(element.Category.Id);
                    // Stacked-wall parents can use a separate category instead
                    // of OST_Walls; keep all actual Wall objects for review.
                    bool candidate = element is Wall || IsCivilCategory(categoryId);
                    if (!candidate)
                    { Exclude(snapshot, element, "不在本期墙、梁、柱、板及待映射通用模型范围"); continue; }
                    FamilyInstance family = element as FamilyInstance;
                    if (categoryId == CategoryId(BuiltInCategory.OST_StructuralFraming) && family != null && family.StructuralType == StructuralType.Brace)
                    { Exclude(snapshot, element, "结构支撑不作为梁计量"); continue; }
                    CivilElementRecord record = CreateRecord(document, element, status);
                    Classify(record, element, categoryId);
                    Level nativeLevel = CivilLevelResolver.Resolve(record, element);
                    if (record.Supported) ReadQuantities(record, element);
                    AddChecks(record, element, levels, nativeLevel);
                    snapshot.Elements.Add(record);
                }
                catch (Exception error)
                {
                    if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                    Exclude(snapshot, element, "读取失败，未计入：" + error.GetType().Name + "：" + error.Message);
                }
            }
            if (snapshot.Elements.Count == 0) snapshot.Notices.Add("当前确认的范围、阶段和设计选项中没有可显示的土建记录；请检查排除原因。");
            if (Revision(document) != snapshot.Revision) snapshot.Notices.Add("读取期间工程已变化，请重新刷新后使用本表。");
            return snapshot;
        }

        private static Document RequireDocument(UIDocument uiDocument)
        {
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null || !document.IsValidObject || document.IsFamilyDocument)
                throw new InvalidOperationException("请打开有效的 Revit 工程模型。");
            return document;
        }

        private static IList<Element> Candidates(UIDocument uiDocument, string scope)
        {
            Document document = uiDocument.Document;
            if (scope == "selection")
            {
                List<Element> selected = new List<Element>();
                foreach (ElementId id in uiDocument.Selection.GetElementIds())
                {
                    Element element = document.GetElement(id);
                    if (element != null && !(element is ElementType)) selected.Add(element);
                }
                return selected;
            }
            if (scope == "view")
            {
                View view = uiDocument.ActiveView;
                if (view == null || view.IsTemplate || !FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
                    throw new InvalidOperationException("当前视图不能用于读取构件，请切换到可迭代的模型视图或使用工程范围。");
                using (FilteredElementCollector collector = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType()) return collector.ToElements();
            }
            using (FilteredElementCollector collector = new FilteredElementCollector(document).WhereElementIsNotElementType()) return collector.ToElements();
        }

        private static CivilElementRecord CreateRecord(Document document, Element element, ElementOnPhaseStatus status)
        {
            ElementType type = document.GetElement(element.GetTypeId()) as ElementType;
            DesignOption option = element.DesignOption;
            return new CivilElementRecord {
                ElementId = Id(element.Id), UniqueId = element.UniqueId, Name = element.Name,
                Category = element.Category == null ? "未分类" : element.Category.Name,
                Family = type == null ? String.Empty : type.FamilyName,
                TypeName = type == null ? String.Empty : type.Name,
                PhaseStatus = status.ToString(), DesignOptionName = option == null ? "主模型" : option.Name,
                Length = NotApplicable("m"), Area = NotApplicable("m2"), Volume = NotApplicable("m3")
            };
        }

        private static bool IsCivilCategory(string categoryId)
        {
            return categoryId == CategoryId(BuiltInCategory.OST_Walls) || categoryId == CategoryId(BuiltInCategory.OST_Floors)
                || categoryId == CategoryId(BuiltInCategory.OST_StructuralFraming) || categoryId == CategoryId(BuiltInCategory.OST_StructuralColumns)
                || categoryId == CategoryId(BuiltInCategory.OST_Columns) || categoryId == CategoryId(BuiltInCategory.OST_GenericModel)
                || categoryId == CategoryId(BuiltInCategory.OST_CurtainWallPanels) || categoryId == CategoryId(BuiltInCategory.OST_CurtainWallMullions);
        }

        private static void Classify(CivilElementRecord record, Element element, string categoryId)
        {
            FamilyInstance family = element as FamilyInstance;
            Wall wall = element as Wall;
            if (family != null && family.SuperComponent != null)
            { Review(record, "嵌套族／子构件存在父子重复风险，本期不计量"); return; }
            if (categoryId == CategoryId(BuiltInCategory.OST_CurtainWallPanels) || categoryId == CategoryId(BuiltInCategory.OST_CurtainWallMullions))
            { Review(record, "幕墙面板／竖梃子构件口径尚未支持，本期不计量"); return; }
            if (wall != null)
            {
                if (wall.IsStackedWall || wall.IsStackedWallMember)
                { Review(record, wall.IsStackedWall ? "叠层墙父构件尚未拆解去重，本期不计量" : "叠层墙成员存在父子重复风险，本期不计量"); return; }
                if (wall.WallType == null || wall.WallType.Kind != WallKind.Basic)
                { Review(record, "幕墙或其他非普通墙口径尚未支持，本期不计量"); return; }
                Supported(record, "wall", "普通墙"); return;
            }
            if (categoryId == CategoryId(BuiltInCategory.OST_Floors) && element is Floor)
            { Supported(record, "floor", "板"); return; }
            if (family != null && categoryId == CategoryId(BuiltInCategory.OST_StructuralFraming) && family.StructuralType == StructuralType.Beam)
            { Supported(record, "beam", "结构梁"); return; }
            if (family != null && categoryId == CategoryId(BuiltInCategory.OST_StructuralColumns))
            {
                if (family.StructuralType == StructuralType.Column) Supported(record, "structural-column", "结构柱");
                else Review(record, "结构柱类别的结构角色未确认为柱，本期不计量");
                return;
            }
            if (family != null && categoryId == CategoryId(BuiltInCategory.OST_Columns))
            { Supported(record, "architectural-column", "建筑柱"); return; }
            Review(record, categoryId == CategoryId(BuiltInCategory.OST_GenericModel) ? "通用模型尚未映射到业务构件，本期不计量" : "此类别的构件形式或结构角色尚未明确支持，本期不计量");
        }

        private static void Supported(CivilElementRecord record, string code, string name)
        { record.KindCode = code; record.KindName = name; record.Supported = true; }
        private static void Review(CivilElementRecord record, string reason)
        {
            record.KindCode = "review"; record.KindName = "待核查／未映射"; record.Supported = false;
            record.ExclusionReason = reason; record.Issues.Add(reason);
        }

        private static void ReadQuantities(CivilElementRecord record, Element element)
        {
            if (record.KindCode == "wall" || record.KindCode == "beam") record.Length = ReadMetric(element, BuiltInParameter.CURVE_ELEM_LENGTH, 1, "m", "CURVE_ELEM_LENGTH");
            if (record.KindCode == "wall" || record.KindCode == "floor") record.Area = ReadMetric(element, BuiltInParameter.HOST_AREA_COMPUTED, 2, "m2", "HOST_AREA_COMPUTED");
            record.Volume = ReadMetric(element, BuiltInParameter.HOST_VOLUME_COMPUTED, 3, "m3", "HOST_VOLUME_COMPUTED");
            CheckMetric(record, "长度", record.Length); CheckMetric(record, "面积", record.Area); CheckMetric(record, "体积", record.Volume);
        }

        private static CivilMetric ReadMetric(Element element, BuiltInParameter id, int dimension, string unit, string source)
        {
            CivilMetric result = new CivilMetric { Unit = unit, Source = source };
            try
            {
                Parameter parameter = element.get_Parameter(id);
                if (parameter == null || !parameter.HasValue) return result;
                if (parameter.StorageType != StorageType.Double) { result.Status = "invalid"; return result; }
                QuantityMetric value = QuantitySnapshot.FromInternalValue(parameter.AsDouble(), dimension);
                result.Status = value.Status; result.Value = value.Value;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                result.Status = "unavailable";
            }
            return result;
        }
        private static CivilMetric NotApplicable(string unit)
        { return new CivilMetric { Unit = unit, Status = "not-applicable", Value = null }; }
        private static void CheckMetric(CivilElementRecord record, string name, CivilMetric metric)
        {
            if (metric.Status == "missing") record.Issues.Add(name + "缺少原生参数值");
            else if (metric.Status == "invalid") record.Issues.Add(name + "原生参数无效");
            else if (metric.Status == "unavailable") record.Issues.Add(name + "原生参数读取失败");
        }

        private static void AddChecks(CivilElementRecord record, Element element, IList<Level> levels, Level nativeLevel)
        {
            try
            {
                if (nativeLevel != null && (Double.IsNaN(nativeLevel.ProjectElevation) || Double.IsInfinity(nativeLevel.ProjectElevation)))
                { record.Issues.Add("原生参照层项目标高无效，未检查跨层"); return; }
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box == null) { record.Issues.Add("无可用模型包围盒，未检查跨层"); return; }
                double minimum = Double.PositiveInfinity, maximum = Double.NegativeInfinity;
                for (int corner = 0; corner < 8; corner++)
                {
                    XYZ point = box.Transform.OfPoint(new XYZ((corner & 1) == 0 ? box.Min.X : box.Max.X,
                        (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z));
                    minimum = Math.Min(minimum, point.Z); maximum = Math.Max(maximum, point.Z);
                }
                if (Double.IsNaN(minimum) || Double.IsInfinity(minimum) || Double.IsNaN(maximum) || Double.IsInfinity(maximum))
                { record.Issues.Add("模型包围盒无效，未检查跨层"); return; }
                bool crosses = levels.Any(level => level.ProjectElevation > minimum + LevelToleranceFeet && level.ProjectElevation < maximum - LevelToleranceFeet
                    && (nativeLevel == null || Math.Abs(level.ProjectElevation - nativeLevel.ProjectElevation) > LevelToleranceFeet));
                if (crosses) record.Issues.Add("跨层候选（仅包围盒检查，尚未分层切量）");
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                record.Issues.Add("包围盒读取失败，未检查跨层");
            }
        }

        private static void CountLinks(Document document, CivilSnapshot snapshot)
        {
            using (FilteredElementCollector collector = new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)))
                foreach (RevitLinkInstance link in collector)
                {
                    snapshot.LinkInstanceCount++;
                    try { if (link.GetLinkDocument() == null) snapshot.MissingLinkCount++; }
                    catch (Exception error)
                    {
                        if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                        snapshot.Notices.Add("链接状态无法读取：" + Id(link.Id));
                    }
                }
            if (snapshot.LinkInstanceCount > 0) snapshot.Notices.Add("根文档共有 " + snapshot.LinkInstanceCount + " 个链接实例，其中 " + snapshot.MissingLinkCount + " 个未加载；所有链接构件均未纳入本期量表。");
        }
        private static void Exclude(CivilSnapshot snapshot, Element element, string reason)
        { snapshot.Exclusions.Add(new CivilExclusion { ElementId = Id(element.Id), Name = element.Name, Reason = reason }); }
        private static ElementId ParseId(string text, bool allowMain)
        {
            long value;
            if (!Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0 && !(allowMain && value == -1))
                throw new InvalidOperationException("阶段或设计选项标识无效，请重新选择。");
#if REVIT_2024_OR_GREATER
            return new ElementId(value);
#else
            if (value > Int32.MaxValue) throw new InvalidOperationException("当前 Revit 版本不支持此标识。");
            return new ElementId((int)value);
#endif
        }
        private static string Id(ElementId id)
        {
            if (id == null) return "-1";
#if REVIT_2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }
        private static string CategoryId(BuiltInCategory category)
        { return ((long)category).ToString(CultureInfo.InvariantCulture); }
        private static string DocumentScope(Document document)
        {
            string identity = document.ProjectInformation == null ? document.Title : document.ProjectInformation.UniqueId;
            return PackageWriter.Hash(identity).Substring(0, 24);
        }
    }
}
