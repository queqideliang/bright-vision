// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace BrightVision.Civil
{
    [DataContract]
    internal sealed class CivilWallJoinBinding
    {
        [DataMember(Order = 1, IsRequired = true)] public string DocumentScope { get; set; }
        [DataMember(Order = 2, IsRequired = true)] public string SourcePath { get; set; }
        [DataMember(Order = 3, IsRequired = true)] public string PhaseId { get; set; }
        [DataMember(Order = 4, IsRequired = true)] public string GeometryViewUniqueId { get; set; }
        [DataMember(Order = 5, IsRequired = true)] public long SourceRevision { get; set; }
        [DataMember(Order = 6, IsRequired = true)] public string GeometryDependencyFingerprint { get; set; }
        public CivilWallJoinBinding() { DocumentScope = SourcePath = PhaseId = GeometryViewUniqueId = GeometryDependencyFingerprint = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilWallJoinPriority
    {
        [DataMember(Order = 1, IsRequired = true)] public string ElementUniqueId { get; set; }
        [DataMember(Order = 2, IsRequired = true)] public int Rank { get; set; }
        public CivilWallJoinPriority() { ElementUniqueId = String.Empty; }
    }

    [DataContract]
    internal sealed class CivilWallJoinPlan
    {
        [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2, IsRequired = true)] public string PlanId { get; set; }
        [DataMember(Order = 3, IsRequired = true)] public string Name { get; set; }
        [DataMember(Order = 4, IsRequired = true)] public int Revision { get; set; }
        [DataMember(Order = 5, IsRequired = true)] public string MethodId { get; set; }
        [DataMember(Order = 6, IsRequired = true)] public CivilWallJoinBinding Binding { get; set; }
        [DataMember(Order = 7, IsRequired = true)] public List<CivilWallJoinPriority> Priorities { get; set; }
        public CivilWallJoinPlan() { SchemaVersion = Revision = 1; PlanId = Name = MethodId = String.Empty; Priorities = new List<CivilWallJoinPriority>(); }
    }

    // Display facts only. These quantities are not accepted as CSG evidence.
    internal sealed class CivilWallJoinParticipant
    {
        public string ElementUniqueId { get; set; }
        public string ElementId { get; set; }
        public string Name { get; set; }
        public bool Requested { get; set; }
        public double? GrossVolumeMetres3 { get; set; }
        public double? OpeningVolumeMetres3 { get; set; }
        public double? NativeVolumeMetres3 { get; set; }
        public CivilWallJoinParticipant() { ElementUniqueId = ElementId = Name = String.Empty; }
    }

    // A separate, source-bound contract. This never relaxes the legacy
    // reject-overlap wall template or silently assigns a business priority.
    internal static class CivilWallJoinPlans
    {
        public const string MethodId = "BV.Wall.ComponentPriority/1";
        public const int MaximumParticipants = 64;
        public const int MaximumBytes = 1024 * 1024;

        public static CivilWallJoinPlan Create(CivilWallJoinBinding binding, IEnumerable<string> explicitOrderedUniqueIds, string name)
        {
            if (explicitOrderedUniqueIds == null) throw new ArgumentNullException("explicitOrderedUniqueIds");
            List<string> order = explicitOrderedUniqueIds.Take(MaximumParticipants + 1).ToList();
            var result = new CivilWallJoinPlan { PlanId = Guid.NewGuid().ToString("D"), Name = name, MethodId = MethodId, Binding = CloneBinding(binding),
                Priorities = order.Select((uid, index) => new CivilWallJoinPriority { ElementUniqueId = uid, Rank = index + 1 }).ToList() };
            Validate(result); return result;
        }

        public static CivilWallJoinBinding CloneBinding(CivilWallJoinBinding value)
        {
            ValidateBinding(value);
            return new CivilWallJoinBinding { DocumentScope = value.DocumentScope, SourcePath = NormalizeSourcePath(value.SourcePath), PhaseId = value.PhaseId,
                GeometryViewUniqueId = value.GeometryViewUniqueId, SourceRevision = value.SourceRevision, GeometryDependencyFingerprint = value.GeometryDependencyFingerprint };
        }

        public static CivilWallJoinPlan Clone(CivilWallJoinPlan value)
        {
            Validate(value);
            return new CivilWallJoinPlan { SchemaVersion = value.SchemaVersion, PlanId = value.PlanId, Name = value.Name, Revision = value.Revision, MethodId = value.MethodId,
                Binding = CloneBinding(value.Binding), Priorities = value.Priorities.Select(entry => new CivilWallJoinPriority { ElementUniqueId = entry.ElementUniqueId, Rank = entry.Rank }).ToList() };
        }

        public static void Validate(CivilWallJoinPlan value)
        {
            if (value == null) throw new ArgumentNullException("value");
            Guid identifier;
            if (value.SchemaVersion != 1 || !Guid.TryParse(value.PlanId, out identifier) || identifier == Guid.Empty || value.Revision < 1 || value.MethodId != MethodId)
                throw new InvalidDataException("交接方案格式、标识、修订或计算方法无效。");
            Text(value.Name, "方案名称", 512, false); ValidateBinding(value.Binding);
            if (value.Priorities == null || value.Priorities.Count < 1 || value.Priorities.Count > MaximumParticipants)
                throw new InvalidDataException("交接方案须包含完整组件的 1 至 64 面参与墙。");
            var ids = new HashSet<string>(StringComparer.Ordinal); var ranks = new HashSet<int>();
            foreach (CivilWallJoinPriority entry in value.Priorities)
            {
                if (entry == null) throw new InvalidDataException("交接优先级记录缺失。");
                Text(entry.ElementUniqueId, "参与墙唯一标识", 1024, false);
                if (!ids.Add(entry.ElementUniqueId) || entry.Rank < 1 || entry.Rank > value.Priorities.Count || !ranks.Add(entry.Rank))
                    throw new InvalidDataException("交接参与墙须唯一，优先级须为不重复的连续 1 至 N。");
            }
        }

        public static void ValidateBinding(CivilWallJoinBinding value)
        {
            if (value == null) throw new InvalidDataException("交接方案缺少来源绑定。");
            Text(value.DocumentScope, "来源工程标识", 1024, false); NormalizeSourcePath(value.SourcePath);
            Text(value.PhaseId, "来源阶段", 1024, false); Text(value.GeometryViewUniqueId, "几何校验视图", 1024, false);
            if (value.SourceRevision < 0 || value.GeometryDependencyFingerprint == null || value.GeometryDependencyFingerprint.Length != 64 ||
                value.GeometryDependencyFingerprint.Any(character => "0123456789abcdef".IndexOf(character) < 0))
                throw new InvalidDataException("交接方案来源修订或依赖几何指纹无效。");
        }

        public static void ValidateFor(CivilWallJoinPlan value, CivilWallJoinBinding current, IEnumerable<string> participantUniqueIds)
        {
            Validate(value); ValidateBinding(current);
            if (!SameIdentity(value.Binding, current) || value.Binding.SourceRevision != current.SourceRevision || value.Binding.GeometryDependencyFingerprint != current.GeometryDependencyFingerprint)
                throw new InvalidDataException("交接方案来源、阶段、视图、模型修订或完整依赖几何已变化，请重新确认顺序。");
            if (participantUniqueIds == null) throw new ArgumentNullException("participantUniqueIds");
            List<string> participants = participantUniqueIds.Take(MaximumParticipants + 1).ToList();
            var identities = new HashSet<string>(participants, StringComparer.Ordinal);
            if (participants.Count < 1 || participants.Count > MaximumParticipants || identities.Count != participants.Count ||
                participants.Any(String.IsNullOrWhiteSpace) || !identities.SetEquals(value.Priorities.Select(entry => entry.ElementUniqueId)))
                throw new InvalidDataException("交接优先级没有准确覆盖完整参与组件，包括计量范围外的依赖墙。");
        }

        // A document-change counter is local to a Revit process. Reopening an
        // unchanged model may reset it. Reuse only the explicit UID order after
        // all persistent source/geometry/member checks; calculated quantities
        // are never imported by this API and ValidateFor remains strict.
        public static CivilWallJoinPlan AdoptForUnchangedGeometry(CivilWallJoinPlan value, CivilWallJoinBinding current, IEnumerable<string> participantUniqueIds)
        {
            CivilWallJoinPlan candidate = Clone(value); CivilWallJoinBinding next = CloneBinding(current);
            if (!SameIdentity(candidate.Binding, next) || candidate.Binding.GeometryDependencyFingerprint != next.GeometryDependencyFingerprint)
                throw new InvalidDataException("来源或完整依赖几何已变化，不能沿用旧交接顺序，请重新确认。");
            CivilWallJoinBinding membershipBinding = CloneBinding(next); membershipBinding.SourceRevision = candidate.Binding.SourceRevision;
            ValidateFor(candidate, membershipBinding, participantUniqueIds);
            if (candidate.Binding.SourceRevision != next.SourceRevision)
            {
                if (candidate.Revision == Int32.MaxValue) throw new InvalidDataException("方案修订已达上限，无法重绑定，请创建新方案。");
                candidate.Binding = next; candidate.Revision++;
            }
            return candidate;
        }

        public static string Fingerprint(CivilWallJoinPlan value)
        {
            Validate(value); var text = new StringBuilder("BV.WallJoinPlan.Canonical/1\n");
            Field(text, "SchemaVersion", value.SchemaVersion.ToString(CultureInfo.InvariantCulture)); Field(text, "PlanId", Guid.Parse(value.PlanId).ToString("D"));
            Field(text, "Name", value.Name); Field(text, "Revision", value.Revision.ToString(CultureInfo.InvariantCulture)); Field(text, "MethodId", value.MethodId);
            Field(text, "DocumentScope", value.Binding.DocumentScope); Field(text, "SourcePath", NormalizeSourcePath(value.Binding.SourcePath).ToUpperInvariant());
            Field(text, "PhaseId", value.Binding.PhaseId); Field(text, "GeometryViewUniqueId", value.Binding.GeometryViewUniqueId);
            Field(text, "SourceRevision", value.Binding.SourceRevision.ToString(CultureInfo.InvariantCulture)); Field(text, "GeometryDependencyFingerprint", value.Binding.GeometryDependencyFingerprint);
            Field(text, "Count", value.Priorities.Count.ToString(CultureInfo.InvariantCulture));
            foreach (CivilWallJoinPriority entry in value.Priorities.OrderBy(item => item.Rank)) { Field(text, "ElementUniqueId", entry.ElementUniqueId); Field(text, "Rank", entry.Rank.ToString(CultureInfo.InvariantCulture)); }
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        public static CivilWallJoinPlan Load(string path)
        {
            path = PlanPath(path); CheckLinks(path); return Parse(ReadBounded(path));
        }

        public static void Save(string path, CivilWallJoinPlan value)
        {
            CivilWallJoinPlan candidate = Clone(value); byte[] bytes;
            using (var memory = new MemoryStream()) { Serializer().WriteObject(memory, candidate); bytes = memory.ToArray(); }
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("交接方案文件超过 1 MiB。");
            path = PlanPath(path); string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("交接方案保存目录不存在。");
            CheckLinks(directory); byte[] previous = null;
            if (File.Exists(path))
            {
                CheckLinks(path); previous = ReadBounded(path); CivilWallJoinPlan saved = Parse(previous);
                if (!String.Equals(saved.PlanId, candidate.PlanId, StringComparison.OrdinalIgnoreCase) || !SameIdentity(saved.Binding, candidate.Binding))
                    throw new InvalidDataException("已有文件属于其他方案或来源，请另存为新文件。");
                if (saved.Revision > candidate.Revision || (saved.Revision == candidate.Revision && Fingerprint(saved) != Fingerprint(candidate)))
                    throw new InvalidDataException("交接方案内容改变须提高修订号，不能覆盖更新版本。");
            }
            string temporary = Path.Combine(directory, ".bv-wall-joins-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                CheckLinks(directory);
                if (previous != null) { CheckLinks(path); if (!previous.SequenceEqual(ReadBounded(path))) throw new InvalidDataException("方案文件在保存前已变化，请重新载入。"); File.Replace(temporary, path, null); }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static CivilWallJoinPlan Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("交接方案为空或超过 1 MiB。");
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 8, MaxStringContentLength = 32768, MaxArrayLength = 128, MaxBytesPerRead = 16384, MaxNameTableCharCount = 16384 };
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas)) { var document = new XmlDocument { XmlResolver = null }; document.Load(reader); Shape(document.DocumentElement, "plan"); }
                using (var memory = new MemoryStream(bytes, false)) { var value = (CivilWallJoinPlan)Serializer().ReadObject(memory); return Clone(value); }
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException || error is InvalidDataException) throw;
                throw new InvalidDataException("BV 墙体交接方案 JSON 无效、含未知字段或已损坏。", error);
            }
        }

        private static void Shape(XmlElement root, string scope)
        {
            if (root == null || root.GetAttribute("type") != "object") throw new InvalidDataException("交接方案节点须为 JSON 对象。");
            string[] strings = scope == "plan" ? new[] { "PlanId", "Name", "MethodId" } : scope == "binding" ? new[] { "DocumentScope", "SourcePath", "PhaseId", "GeometryViewUniqueId", "GeometryDependencyFingerprint" } : new[] { "ElementUniqueId" };
            string[] numbers = scope == "plan" ? new[] { "SchemaVersion", "Revision" } : scope == "binding" ? new[] { "SourceRevision" } : new[] { "Rank" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in root.ChildNodes)
            {
                var field = node as XmlElement;
                if (field == null || !seen.Add(field.Name)) throw new InvalidDataException("交接方案包含无效或重复 JSON 字段。");
                if (strings.Contains(field.Name, StringComparer.Ordinal)) { string kind = field.GetAttribute("type"); if (kind.Length != 0 && kind != "string") throw new InvalidDataException("交接方案字段须为字符串。"); }
                else if (numbers.Contains(field.Name, StringComparer.Ordinal))
                {
                    long integer;
                    if (field.GetAttribute("type") != "number" || !Int64.TryParse(field.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer) ||
                        (field.Name != "SourceRevision" && (integer < Int32.MinValue || integer > Int32.MaxValue))) throw new InvalidDataException("交接方案字段须为范围内的整数。");
                }
                else if (scope == "plan" && field.Name == "Binding") Shape(field, "binding");
                else if (scope == "plan" && field.Name == "Priorities")
                {
                    if (field.GetAttribute("type") != "array" || field.ChildNodes.Count > MaximumParticipants) throw new InvalidDataException("交接优先级须为最多 64 项数组。");
                    foreach (XmlNode item in field.ChildNodes) { var entry = item as XmlElement; if (entry == null || entry.Name != "item") throw new InvalidDataException("交接优先级节点无效。"); Shape(entry, "priority"); }
                }
                else throw new InvalidDataException("交接方案包含未知 JSON 字段：" + field.Name);
            }
            int count = strings.Length + numbers.Length + (scope == "plan" ? 2 : 0);
            if (seen.Count != count) throw new InvalidDataException("交接方案缺少必需 JSON 字段。");
        }

        private static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(CivilWallJoinPlan), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 10000 }); }
        private static byte[] ReadBounded(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length <= 0 || file.Length > MaximumBytes) throw new InvalidDataException("交接方案为空或超过 1 MiB。");
                using (var memory = new MemoryStream()) { byte[] buffer = new byte[16384]; int read; while ((read = file.Read(buffer, 0, buffer.Length)) > 0) { if (memory.Length > MaximumBytes - read) throw new InvalidDataException("交接方案超过 1 MiB。"); memory.Write(buffer, 0, read); } return memory.ToArray(); }
            }
        }
        private static bool SameIdentity(CivilWallJoinBinding left, CivilWallJoinBinding right)
        { return left.DocumentScope == right.DocumentScope && String.Equals(NormalizeSourcePath(left.SourcePath), NormalizeSourcePath(right.SourcePath), StringComparison.OrdinalIgnoreCase) && left.PhaseId == right.PhaseId && left.GeometryViewUniqueId == right.GeometryViewUniqueId; }
        private static void Field(StringBuilder text, string key, string value) { text.Append(key).Append(':').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n'); }
        private static void Text(string value, string label, int maximum, bool empty)
        {
            if (value == null || value.Length > maximum || (!empty && String.IsNullOrWhiteSpace(value)) || value.Any(Char.IsControl)) throw new InvalidDataException(label + "无效。");
            try { new UTF8Encoding(false, true).GetByteCount(value); } catch (EncoderFallbackException error) { throw new InvalidDataException(label + "含无效 Unicode。", error); }
        }
        private static string NormalizeSourcePath(string value)
        {
            Text(value, "来源路径", 32767, true); if (value.Length == 0) return String.Empty;
            if (!Path.IsPathRooted(value) || value.StartsWith("\\\\?\\", StringComparison.Ordinal) || value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                !(value.StartsWith("\\\\", StringComparison.Ordinal) || (value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))))
                throw new InvalidDataException("来源路径须为普通绝对路径，未保存模型可使用空路径。");
            string full; try { full = Path.GetFullPath(value); } catch (Exception error) { if (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) throw new InvalidDataException("来源路径无效。", error); throw; }
            if (full.Substring(Path.GetPathRoot(full).Length).Contains(":")) throw new InvalidDataException("来源路径不能使用备用数据流。"); return full;
        }
        private static string PlanPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !path.EndsWith(".bvwalljoins.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 .bvwalljoins.json 交接方案文件。");
            return NormalizeSourcePath(path);
        }
        private static void CheckLinks(string path)
        {
            for (string cursor = path; !String.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor)) { if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("交接方案路径不能使用符号链接或目录联接。"); if (Path.GetDirectoryName(cursor) == cursor) break; }
        }
    }
}
