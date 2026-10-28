using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BrightVision.Civil
{
    [DataContract]
    internal sealed class CivilQuantityLevel
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        [DataMember(Order = 3)] public double? ElevationMetres { get; set; }
        public CivilQuantityLevel() { Id = Name = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilLevelMapping
    {
        [DataMember(Order = 1)] public string SourceLevelUniqueId { get; set; }
        [DataMember(Order = 2)] public string QuantityLevelId { get; set; }
        public CivilLevelMapping() { SourceLevelUniqueId = QuantityLevelId = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilSegment
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        public CivilSegment() { Id = Name = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilElementSegment
    {
        [DataMember(Order = 1)] public string ElementUniqueId { get; set; }
        [DataMember(Order = 2)] public string SegmentId { get; set; }
        public CivilElementSegment() { ElementUniqueId = SegmentId = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilProjectSettings
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2)] public string ProjectId { get; set; }
        [DataMember(Order = 3)] public string Name { get; set; }
        [DataMember(Order = 4)] public string SourceDocumentScope { get; set; }
        [DataMember(Order = 5)] public string SourcePath { get; set; }
        [DataMember(Order = 6)] public int Revision { get; set; }
        [DataMember(Order = 7)] public List<CivilQuantityLevel> QuantityLevels { get; set; }
        [DataMember(Order = 8)] public List<CivilLevelMapping> LevelMappings { get; set; }
        [DataMember(Order = 9)] public List<CivilSegment> Segments { get; set; }
        [DataMember(Order = 10)] public List<CivilElementSegment> ElementSegments { get; set; }
        [DataMember(Order = 11, EmitDefaultValue = false)] public string CreatedAtUtc { get; set; }
        [DataMember(Order = 12, EmitDefaultValue = false)] public string UpdatedAtUtc { get; set; }
        [DataMember(Order = 13, EmitDefaultValue = false)] public string PreferredScope { get; set; }
        [DataMember(Order = 14, EmitDefaultValue = false)] public string PhaseId { get; set; }
        [DataMember(Order = 15, EmitDefaultValue = false)] public string DesignOptionId { get; set; }
        public CivilProjectSettings()
        {
            SchemaVersion = 1; ProjectId = Name = SourceDocumentScope = SourcePath = String.Empty;
            QuantityLevels = new List<CivilQuantityLevel>(); LevelMappings = new List<CivilLevelMapping>();
            Segments = new List<CivilSegment>(); ElementSegments = new List<CivilElementSegment>();
        }
    }

    internal static class CivilProject
    {
        private const string MissingQuantityLevel = "计量层未映射（工程设置）";
        private const string MissingSegment = "施工段未分配（工程设置）";

        public static CivilProjectSettings Create(CivilSnapshot snapshot, string name)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var profile = new CivilProjectSettings {
                ProjectId = Guid.NewGuid().ToString("D"), Name = String.IsNullOrWhiteSpace(name) ? snapshot.ModelTitle : name.Trim(),
                SourceDocumentScope = snapshot.DocumentScope, SourcePath = NormalizeSourcePath(snapshot.SourcePath),
                Revision = 1, CreatedAtUtc = now, UpdatedAtUtc = now, PreferredScope = "view", DesignOptionId = "-1"
            };
            Validate(profile);
            return profile;
        }

        public static void Validate(CivilProjectSettings profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (profile.SchemaVersion != 1) throw new InvalidDataException("不支持的计量工程格式版本。");
            Guid id;
            if (!Guid.TryParse(profile.ProjectId, out id) || id == Guid.Empty) throw new InvalidDataException("计量工程标识无效。");
            Required(profile.Name, "工程名称", 512); Required(profile.SourceDocumentScope, "来源文档标识", 1024);
            NormalizeSourcePath(profile.SourcePath);
            if (profile.Revision < 1) throw new InvalidDataException("计量工程修订号无效。");
            Timestamp(profile.CreatedAtUtc); Timestamp(profile.UpdatedAtUtc);
            if (!String.IsNullOrEmpty(profile.PreferredScope) && profile.PreferredScope != "view" && profile.PreferredScope != "selection" && profile.PreferredScope != "project")
                throw new InvalidDataException("读取范围设置无效。");
            Optional(profile.PhaseId, "阶段标识", 1024); Optional(profile.DesignOptionId, "设计选项标识", 1024);
            Collection(profile.QuantityLevels, "计量层"); Collection(profile.LevelMappings, "楼层映射");
            Collection(profile.Segments, "施工段"); Collection(profile.ElementSegments, "构件施工段归属");
            var levels = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilQuantityLevel level in profile.QuantityLevels)
            {
                if (level == null) throw new InvalidDataException("计量层记录不能为空。");
                Required(level.Id, "计量层标识", 1024); Required(level.Name, "计量层名称", 512);
                if (!levels.Add(level.Id)) throw new InvalidDataException("计量层标识重复。");
                if (level.ElevationMetres.HasValue && (Double.IsNaN(level.ElevationMetres.Value) || Double.IsInfinity(level.ElevationMetres.Value)))
                    throw new InvalidDataException("计量层标高必须是有限米制值。");
            }
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilLevelMapping mapping in profile.LevelMappings)
            {
                if (mapping == null) throw new InvalidDataException("楼层映射记录不能为空。");
                Required(mapping.SourceLevelUniqueId, "来源楼层唯一标识", 1024); Required(mapping.QuantityLevelId, "目标计量层标识", 1024);
                if (!mapped.Add(mapping.SourceLevelUniqueId)) throw new InvalidDataException("同一来源楼层不能映射到多个计量层。");
                if (!levels.Contains(mapping.QuantityLevelId)) throw new InvalidDataException("楼层映射引用了不存在的计量层。");
            }
            var segments = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilSegment segment in profile.Segments)
            {
                if (segment == null) throw new InvalidDataException("施工段记录不能为空。");
                Required(segment.Id, "施工段标识", 1024); Required(segment.Name, "施工段名称", 512);
                if (!segments.Add(segment.Id)) throw new InvalidDataException("施工段标识重复。");
            }
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilElementSegment assignment in profile.ElementSegments)
            {
                if (assignment == null) throw new InvalidDataException("施工段归属记录不能为空。");
                Required(assignment.ElementUniqueId, "来源构件唯一标识", 1024); Required(assignment.SegmentId, "目标施工段标识", 1024);
                if (!assigned.Add(assignment.ElementUniqueId)) throw new InvalidDataException("同一构件只能归属一个施工段。");
                if (!segments.Contains(assignment.SegmentId)) throw new InvalidDataException("构件归属引用了不存在的施工段。");
            }
        }

        // Applies only reporting annotations. Native dimensions, source IDs,
        // original levels and the resolved Revit reference level are untouched.
        public static CivilSnapshot Apply(CivilSnapshot snapshot, CivilProjectSettings profile)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            Validate(profile);
            if (!String.Equals(snapshot.DocumentScope, profile.SourceDocumentScope, StringComparison.Ordinal) ||
                !String.Equals(NormalizeSourcePath(snapshot.SourcePath), NormalizeSourcePath(profile.SourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("计量工程与当前模型来源不一致，请使用该模型对应的工程设置。");
            var levels = profile.QuantityLevels.ToDictionary(level => level.Id, StringComparer.Ordinal);
            snapshot = CopySnapshot(snapshot);
            var mappings = profile.LevelMappings.ToDictionary(mapping => mapping.SourceLevelUniqueId, mapping => mapping.QuantityLevelId, StringComparer.Ordinal);
            var segments = profile.Segments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
            var assignments = profile.ElementSegments.ToDictionary(assignment => assignment.ElementUniqueId, assignment => assignment.SegmentId, StringComparer.Ordinal);
            foreach (CivilElementRecord element in snapshot.Elements ?? new List<CivilElementRecord>())
            {
                if (element == null) continue;
                string target;
                element.QuantityLevelId = mappings.TryGetValue(element.LevelUniqueId ?? String.Empty, out target) ? target : String.Empty;
                element.QuantityLevelName = element.QuantityLevelId.Length == 0 ? "未映射计量层" : levels[element.QuantityLevelId].Name;
                element.SegmentId = assignments.TryGetValue(element.UniqueId ?? String.Empty, out target) ? target : String.Empty;
                element.SegmentName = element.SegmentId.Length == 0 ? "未分配施工段" : segments[element.SegmentId].Name;
                element.ProjectIssues = new List<string>();
                if (element.QuantityLevelId.Length == 0) element.ProjectIssues.Add(MissingQuantityLevel);
                if (element.SegmentId.Length == 0) element.ProjectIssues.Add(MissingSegment);
            }
            snapshot.ProjectNotices = new List<string>();
            int missingLevel = snapshot.Elements.Count(element => element != null && String.IsNullOrEmpty(element.QuantityLevelId));
            int missingSegment = snapshot.Elements.Count(element => element != null && String.IsNullOrEmpty(element.SegmentId));
            if (missingLevel > 0) snapshot.ProjectNotices.Add(missingLevel.ToString(CultureInfo.InvariantCulture) + " 个当前范围构件尚未映射计量层；原生量仍保留。");
            if (missingSegment > 0) snapshot.ProjectNotices.Add(missingSegment.ToString(CultureInfo.InvariantCulture) + " 个当前范围构件尚未分配施工段；未执行几何切分。");
            snapshot.ProjectId = profile.ProjectId; snapshot.ProjectName = profile.Name; snapshot.ProjectRevision = profile.Revision;
            return snapshot;
        }

        public static void AssignSegment(CivilProjectSettings profile, IEnumerable<CivilElementRecord> elements, string segmentId)
        {
            Validate(profile);
            segmentId = segmentId ?? String.Empty;
            if (segmentId.Length > 0 && !profile.Segments.Any(segment => segment.Id == segmentId)) throw new InvalidDataException("目标施工段不存在。");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilElementRecord element in elements ?? new CivilElementRecord[0])
            {
                if (element == null) throw new InvalidDataException("不能分配空构件。");
                Required(element.UniqueId, "来源构件唯一标识", 1024); identities.Add(element.UniqueId);
            }
            var next = profile.ElementSegments.Where(assignment => !identities.Contains(assignment.ElementUniqueId)).ToList();
            if (segmentId.Length > 0) foreach (string identity in identities.OrderBy(value => value, StringComparer.Ordinal))
                next.Add(new CivilElementSegment { ElementUniqueId = identity, SegmentId = segmentId });
            if (next.Count > 50000) throw new InvalidDataException("构件施工段归属超过上限；工程设置未变更。");
            var previous = profile.ElementSegments.ToDictionary(assignment => assignment.ElementUniqueId, assignment => assignment.SegmentId, StringComparer.Ordinal);
            bool changed = next.Count != previous.Count || next.Any(assignment => {
                string existing; return !previous.TryGetValue(assignment.ElementUniqueId, out existing) || existing != assignment.SegmentId;
            });
            if (!changed) return;
            if (profile.Revision == Int32.MaxValue) throw new InvalidDataException("计量工程修订号已达到上限。");
            profile.ElementSegments = next;
            Touch(profile);
        }

        public static void ClearAssignment(CivilProjectSettings profile, IEnumerable<CivilElementRecord> elements) { AssignSegment(profile, elements, String.Empty); }

        public static CivilProjectSettings Clone(CivilProjectSettings profile) { Validate(profile); return CivilProjectStore.CloneValue(profile); }

        public static void Touch(CivilProjectSettings profile)
        {
            Validate(profile);
            if (profile.Revision == Int32.MaxValue) throw new InvalidDataException("计量工程修订号已达到上限。");
            profile.Revision++; profile.UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        internal static string NormalizeSourcePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 32767 || value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                !AbsoluteFilePath(value) || !value.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请先保存本地或 UNC 路径的 RVT 模型；未保存、云模型及 Revit Server 路径尚不支持工程绑定。");
            try
            {
                string full = Path.GetFullPath(value);
                string root = Path.GetPathRoot(full);
                if (full.Substring(root.Length).IndexOf(':') >= 0 || root.Length < 3 || (value.Length > 1 && value[1] == ':' && (value.Length < 3 || (value[2] != '\\' && value[2] != '/'))))
                    throw new InvalidDataException("模型来源必须使用绝对文件路径。");
                return full;
            }
            catch (Exception error)
            {
                if (error is InvalidDataException) throw;
                if (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) throw new InvalidDataException("模型来源路径不受支持。", error);
                throw;
            }
        }

        private static void Required(string value, string label, int maximum)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > maximum || value.IndexOf('\0') >= 0) throw new InvalidDataException(label + "无效。");
        }
        private static void Optional(string value, string label, int maximum) { if (value != null && value.Length > 0) Required(value, label, maximum); }
        internal static bool AbsoluteFilePath(string value) { return !String.IsNullOrEmpty(value) && Path.IsPathRooted(value) && (value.StartsWith("\\\\", StringComparison.Ordinal) || (value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))); }
        private static void Collection<T>(List<T> values, string label) { if (values == null || values.Count > 50000) throw new InvalidDataException(label + "列表缺失或超过上限。"); }
        private static void Timestamp(string value)
        {
            if (String.IsNullOrEmpty(value)) return;
            DateTimeOffset result;
            if (value.Length > 64 || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result) || result.Offset != TimeSpan.Zero || value.IndexOf('T') < 0)
                throw new InvalidDataException("工程时间必须是 UTC ISO 日期。");
        }

        private static CivilSnapshot CopySnapshot(CivilSnapshot source)
        {
            return new CivilSnapshot {
                ModelTitle = source.ModelTitle, DocumentScope = source.DocumentScope, SourcePath = source.SourcePath,
                CapturedAtUtc = source.CapturedAtUtc, ScopeLabel = source.ScopeLabel, PhaseLabel = source.PhaseLabel, DesignOptionLabel = source.DesignOptionLabel,
                CandidateCount = source.CandidateCount, LinkInstanceCount = source.LinkInstanceCount, MissingLinkCount = source.MissingLinkCount, Revision = source.Revision,
                Notices = new List<string>(source.Notices ?? new List<string>()),
                Exclusions = (source.Exclusions ?? new List<CivilExclusion>()).Where(item => item != null).Select(item => new CivilExclusion { ElementId = item.ElementId, Name = item.Name, Reason = item.Reason }).ToList(),
                Levels = (source.Levels ?? new List<CivilLevelInfo>()).Where(item => item != null).Select(item => new CivilLevelInfo { Id = item.Id, UniqueId = item.UniqueId, Name = item.Name, ElevationMetres = item.ElevationMetres }).ToList(),
                Elements = (source.Elements ?? new List<CivilElementRecord>()).Where(item => item != null).Select(item => new CivilElementRecord {
                    ElementId = item.ElementId, UniqueId = item.UniqueId, Name = item.Name, Category = item.Category, KindCode = item.KindCode, KindName = item.KindName,
                    Family = item.Family, TypeName = item.TypeName, LevelId = item.LevelId, LevelName = item.LevelName, RawLevelId = item.RawLevelId, RawLevelName = item.RawLevelName,
                    LevelUniqueId = item.LevelUniqueId, LevelSource = item.LevelSource, PhaseStatus = item.PhaseStatus, DesignOptionName = item.DesignOptionName,
                    ExclusionReason = item.ExclusionReason, Supported = item.Supported, Issues = new List<string>(item.Issues ?? new List<string>()),
                    Length = item.Length, Area = item.Area, Volume = item.Volume
                }).ToList()
            };
        }
    }

    internal static class CivilProjectStore
    {
        public const int MaxBytes = 4 * 1024 * 1024;

        public static CivilProjectSettings Load(string path)
        {
            path = ProjectPath(path);
            CheckLinks(path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length <= 0 || file.Length > MaxBytes) throw new InvalidDataException("工程设置文件为空或超过 4 MiB。");
                using (var memory = new BoundedMemoryStream())
                {
                    byte[] buffer = new byte[65536]; int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, count);
                    memory.Position = 0;
                    CivilProjectSettings profile;
                    try { profile = (CivilProjectSettings)Serializer().ReadObject(memory); }
                    catch (Exception error) { throw new InvalidDataException("工程设置 JSON 无效或已损坏。", error); }
                    CivilProject.Validate(profile);
                    return profile;
                }
            }
        }

        public static void Save(string path, CivilProjectSettings profile)
        {
            CivilProject.Validate(profile);
            path = ProjectPath(path);
            string folder = Path.GetDirectoryName(path);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("工程设置保存目录不存在。");
            CheckLinks(folder);
            bool replacing = File.Exists(path);
            if (replacing)
            {
                // Never replace a damaged project with a fresh empty profile.
                CivilProjectSettings previous = Load(path);
                if (!String.Equals(previous.ProjectId, profile.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                    previous.SourceDocumentScope != profile.SourceDocumentScope ||
                    !String.Equals(CivilProject.NormalizeSourcePath(previous.SourcePath), CivilProject.NormalizeSourcePath(profile.SourcePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已有文件属于另一计量工程，不能覆盖。");
                if (previous.Revision > profile.Revision) throw new InvalidDataException("已有工程文件修订号较新，请重新载入后再保存。");
            }
            string temporary = Path.Combine(folder, ".bright-civil-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var memory = new BoundedMemoryStream())
                {
                    Serializer().WriteObject(memory, profile);
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        memory.Position = 0; memory.CopyTo(file); file.Flush(true);
                    }
                }
                if (replacing)
                {
                    CivilProjectSettings current = Load(path);
                    if (!String.Equals(current.ProjectId, profile.ProjectId, StringComparison.OrdinalIgnoreCase) || current.Revision > profile.Revision)
                        throw new InvalidDataException("工程文件已发生变化，请重新载入。");
                    File.Replace(temporary, path, null);
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static DataContractJsonSerializer Serializer()
        {
            return new DataContractJsonSerializer(typeof(CivilProjectSettings), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 1000000 });
        }
        internal static CivilProjectSettings CloneValue(CivilProjectSettings profile)
        {
            using (var memory = new BoundedMemoryStream())
            {
                Serializer().WriteObject(memory, profile); memory.Position = 0;
                var clone = (CivilProjectSettings)Serializer().ReadObject(memory); CivilProject.Validate(clone); return clone;
            }
        }
        private static string ProjectPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !CivilProject.AbsoluteFilePath(value) || !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new InvalidDataException("请选择绝对路径的 JSON 工程设置文件。");
            string path = Path.GetFullPath(value); string root = Path.GetPathRoot(path);
            if (path.Substring(root.Length).IndexOf(':') >= 0) throw new InvalidDataException("工程文件路径不受支持。");
            return path;
        }
        private static void CheckLinks(string path)
        {
            string current = path;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("工程文件及目录不能使用符号链接或目录联接。");
                string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
        private sealed class BoundedMemoryStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position > MaxBytes - count) throw new InvalidDataException("工程设置超过 4 MiB。");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= MaxBytes) throw new InvalidDataException("工程设置超过 4 MiB。");
                base.WriteByte(value);
            }
        }
    }
}
