using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    // Native instance references only. Parameter order identifies provenance
    // when references agree; it never resolves a disagreement by precedence.
    internal static class CivilLevelResolver
    {
        private sealed class Reference
        {
            internal readonly string Source;
            internal readonly Level Level;
            internal Reference(string source, Level level) { Source = source; Level = level; }
        }

        internal static Level Resolve(CivilElementRecord record, Element element)
        {
            Document document = element.Document;
            ElementId rawId = ElementId.InvalidElementId;
            Level rawLevel = null;
            try
            {
                rawId = element.LevelId;
                rawLevel = document.GetElement(rawId) as Level;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                record.Issues.Add("原始楼层 Element.LevelId 读取失败：" + error.GetType().Name);
            }
            record.RawLevelId = Id(rawId);
            record.RawLevelName = rawLevel == null ? String.Empty : rawLevel.Name;
            Assign(record, rawLevel, rawLevel == null ? String.Empty : "Element.LevelId");

            List<Reference> references = new List<Reference>();
            bool incomplete = false;
            if (record.KindCode == "wall")
                ReadReference(element, BuiltInParameter.WALL_BASE_CONSTRAINT, "WALL_BASE_CONSTRAINT", references, record, ref incomplete);
            else if (record.KindCode == "floor")
                ReadReference(element, BuiltInParameter.LEVEL_PARAM, "LEVEL_PARAM", references, record, ref incomplete);
            else if (record.KindCode == "beam")
            {
                ReadReference(element, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, "INSTANCE_REFERENCE_LEVEL_PARAM", references, record, ref incomplete);
                ReadReference(element, BuiltInParameter.FAMILY_LEVEL_PARAM, "FAMILY_LEVEL_PARAM", references, record, ref incomplete);
                ReadReference(element, BuiltInParameter.SCHEDULE_LEVEL_PARAM, "SCHEDULE_LEVEL_PARAM", references, record, ref incomplete);
            }
            else if (record.KindCode == "structural-column" || record.KindCode == "architectural-column")
            {
                ReadReference(element, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, "FAMILY_BASE_LEVEL_PARAM", references, record, ref incomplete);
                ReadReference(element, BuiltInParameter.FAMILY_LEVEL_PARAM, "FAMILY_LEVEL_PARAM", references, record, ref incomplete);
                ReadReference(element, BuiltInParameter.SCHEDULE_LEVEL_PARAM, "SCHEDULE_LEVEL_PARAM", references, record, ref incomplete);
            }

            if (rawLevel != null)
            {
                List<Reference> different = references.Where(reference => !reference.Level.Id.Equals(rawLevel.Id)).ToList();
                if (different.Count > 0)
                    record.Issues.Add("原生楼层冲突：保留 Element.LevelId=" + Id(rawLevel.Id) + "；" + Describe(different));
                return rawLevel;
            }
            int distinct = references.Select(reference => Id(reference.Level.Id)).Distinct(StringComparer.Ordinal).Count();
            if (distinct > 1)
            {
                record.Issues.Add("原生参照楼层冲突，未指定楼层：" + Describe(references) + "；原始楼层无效，未按参数顺序或几何推断归属");
                return null;
            }
            if (references.Count > 0 && !incomplete)
            {
                Reference selected = references[0];
                Assign(record, selected.Level, selected.Source);
                return selected.Level;
            }
            record.Issues.Add(incomplete
                ? "未指定有效原始楼层，且原生参照层存在不可核对的值；未按几何推断归属"
                : "未指定有效原始楼层，且无可用原生参照层；未按几何推断归属");
            return null;
        }

        private static void ReadReference(Element element, BuiltInParameter parameterId, string source,
            IList<Reference> references, CivilElementRecord record, ref bool incomplete)
        {
            try
            {
                Parameter parameter = element.get_Parameter(parameterId);
                if (parameter == null || !parameter.HasValue) return;
                if (parameter.StorageType != StorageType.ElementId)
                {
                    incomplete = true;
                    record.Issues.Add("原生参照层参数类型无效：" + source);
                    return;
                }
                ElementId id = parameter.AsElementId();
                if (id == null || id.Equals(ElementId.InvalidElementId)) return;
                Level level = element.Document.GetElement(id) as Level;
                if (level == null)
                {
                    incomplete = true;
                    record.Issues.Add("原生参照层未指向当前工程 Level：" + source + "=" + Id(id));
                    return;
                }
                references.Add(new Reference(source, level));
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                incomplete = true;
                record.Issues.Add("原生参照层读取失败：" + source + "（" + error.GetType().Name + "）");
            }
        }

        private static void Assign(CivilElementRecord record, Level level, string source)
        {
            record.LevelId = level == null ? "-1" : Id(level.Id);
            record.LevelName = level == null ? "未指定楼层" : level.Name;
            record.LevelUniqueId = level == null ? String.Empty : level.UniqueId;
            record.LevelSource = source;
        }
        private static string Describe(IEnumerable<Reference> references)
        { return String.Join("；", references.Select(reference => reference.Source + "=" + Id(reference.Level.Id))); }
        private static string Id(ElementId id)
        {
            if (id == null) return "-1";
#if REVIT_2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }
    }
}
