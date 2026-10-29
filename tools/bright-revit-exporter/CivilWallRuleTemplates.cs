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
    internal sealed class CivilWallPriority
    {
        [DataMember(Order = 1, IsRequired = true)] public string ElementUniqueId { get; set; }
        [DataMember(Order = 2, IsRequired = true)] public int Rank { get; set; }
        public CivilWallPriority() { ElementUniqueId = String.Empty; }
    }

    // BV-owned project conventions, not regional bill/quota rules. This file
    // contains no copied vendor templates and accepts no scripts or expressions.
    [DataContract]
    internal sealed class CivilWallRuleTemplate
    {
        [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2, IsRequired = true)] public string TemplateId { get; set; }
        [DataMember(Order = 3, IsRequired = true)] public string Name { get; set; }
        [DataMember(Order = 4, IsRequired = true)] public string Description { get; set; }
        [DataMember(Order = 5, IsRequired = true)] public string MethodId { get; set; }
        [DataMember(Order = 6, IsRequired = true)] public string AreaMethodId { get; set; }
        [DataMember(Order = 7, IsRequired = true)] public string OpeningPolicy { get; set; }
        [DataMember(Order = 8, IsRequired = true)] public string JoinPolicy { get; set; }
        [DataMember(Order = 9, IsRequired = true)] public int Revision { get; set; }
        [DataMember(Order = 10, IsRequired = true)] public double AbsoluteToleranceMetres3 { get; set; }
        [DataMember(Order = 11, IsRequired = true)] public double RelativeTolerance { get; set; }
        [DataMember(Order = 12, IsRequired = true)] public List<CivilWallPriority> Priorities { get; set; }
        public CivilWallRuleTemplate()
        {
            SchemaVersion = Revision = 1;
            TemplateId = Name = Description = MethodId = AreaMethodId = OpeningPolicy = JoinPolicy = String.Empty;
            Priorities = new List<CivilWallPriority>();
        }
    }

    internal static class CivilWallRuleTemplates
    {
        public const int MaxBytes = 1024 * 1024;
        public const string VolumeMethodId = "BV.Wall.RectSolid/1";
        public const string ProjectionAreaMethodId = "BV.Wall.AxisProjection/1";
        public const string VerifiedRectCutout = "verified-rect-cutout";
        public const string NativeOpeningOnly = "native-opening-only";
        public const string RejectOverlap = "reject-overlap";
        public const double MaximumAbsoluteToleranceMetres3 = 1e-8;
        public const double MaximumRelativeTolerance = 1e-8;

        public static CivilWallRuleTemplate Default()
        {
            return new CivilWallRuleTemplate {
                TemplateId = "BV.Wall.ProjectGeometry", Name = "BV 项目几何核对",
                Description = "崛亮视觉 · Bright Vision 自有项目约定；独立矩形基准与实际实体核对，允许经证明的矩形实际切洞；不是地区清单或定额规则。",
                MethodId = VolumeMethodId, AreaMethodId = ProjectionAreaMethodId,
                OpeningPolicy = VerifiedRectCutout, JoinPolicy = RejectOverlap,
                AbsoluteToleranceMetres3 = MaximumAbsoluteToleranceMetres3, RelativeTolerance = MaximumRelativeTolerance
            };
        }

        public static CivilWallRuleTemplate LegacyExplicitOpeningOnly()
        {
            CivilWallRuleTemplate value = Default();
            value.TemplateId = "BV.Wall.LegacyExplicitOpeningOnly";
            value.Name = "BV 原生矩形 Opening 核对";
            value.Description = "崛亮视觉 · Bright Vision 自有项目约定；只接受明确的原生矩形 Opening，门窗族实际切洞不纳入本方案；不是地区清单或定额规则。";
            value.OpeningPolicy = NativeOpeningOnly;
            return value;
        }

        public static IList<CivilWallRuleTemplate> Catalog()
        {
            return new List<CivilWallRuleTemplate> { Default(), LegacyExplicitOpeningOnly() };
        }

        public static bool IsBuiltIn(CivilWallRuleTemplate value) { return value != null && IsBuiltIn(value.TemplateId); }
        public static bool IsBuiltIn(string templateId) { return templateId == "BV.Wall.ProjectGeometry" || templateId == "BV.Wall.LegacyExplicitOpeningOnly"; }

        public static void Validate(CivilWallRuleTemplate value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.SchemaVersion != 1) throw new InvalidDataException("不支持的 BV 墙体规则模板格式版本。");
            Required(value.TemplateId, "模板标识", 256); Required(value.Name, "模板名称", 512);
            Text(value.Description, "模板说明", 8192, true);
            if (value.Revision < 1) throw new InvalidDataException("模板修订号必须为正整数。");
            if (value.MethodId != VolumeMethodId || value.AreaMethodId != ProjectionAreaMethodId)
                throw new InvalidDataException("模板引用了尚未实现的墙体体积或投影面积方法。");
            if (value.OpeningPolicy != VerifiedRectCutout && value.OpeningPolicy != NativeOpeningOnly)
                throw new InvalidDataException("模板切洞策略未实现。");
            if (value.JoinPolicy != RejectOverlap) throw new InvalidDataException("当前模板仅支持拒绝重叠，交接优先级分配方法尚未实现。");
            Tolerance(value.AbsoluteToleranceMetres3, MaximumAbsoluteToleranceMetres3, "绝对体积容差");
            Tolerance(value.RelativeTolerance, MaximumRelativeTolerance, "相对体积容差");
            if (value.Priorities == null || value.Priorities.Count > 20000) throw new InvalidDataException("模板优先级集合缺失或超过上限。");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var ranks = new HashSet<int>();
            foreach (CivilWallPriority priority in value.Priorities)
            {
                if (priority == null) throw new InvalidDataException("模板优先级记录不能为空。");
                Required(priority.ElementUniqueId, "优先级构件唯一标识", 1024);
                if (!identities.Add(priority.ElementUniqueId)) throw new InvalidDataException("优先级构件唯一标识重复。");
                if (priority.Rank < 1 || !ranks.Add(priority.Rank)) throw new InvalidDataException("优先级顺序必须为不同的正整数。");
            }
            if (value.Priorities.Count != 0) throw new InvalidDataException("当前拒绝重叠策略不使用优先级；尚未实现的分配配置不能保存或计算。");
        }

        public static CivilWallRuleTemplate Clone(CivilWallRuleTemplate value)
        {
            Validate(value);
            return new CivilWallRuleTemplate {
                SchemaVersion = value.SchemaVersion, TemplateId = value.TemplateId, Name = value.Name, Description = value.Description,
                MethodId = value.MethodId, AreaMethodId = value.AreaMethodId, OpeningPolicy = value.OpeningPolicy, JoinPolicy = value.JoinPolicy,
                Revision = value.Revision, AbsoluteToleranceMetres3 = value.AbsoluteToleranceMetres3, RelativeTolerance = value.RelativeTolerance,
                Priorities = value.Priorities.Select(item => new CivilWallPriority { ElementUniqueId = item.ElementUniqueId, Rank = item.Rank }).ToList()
            };
        }

        public static CivilWallRuleTemplate Copy(CivilWallRuleTemplate value, string newName)
        {
            CivilWallRuleTemplate copied = Clone(value);
            copied.TemplateId = Guid.NewGuid().ToString("D"); copied.Revision = 1; copied.Name = newName;
            Validate(copied); return copied;
        }

        // All fields, including identity, revision, name and description, are
        // bound. Fixed keys, length-prefixed text and fixed-width IEEE binary64
        // hexadecimal numbers make the hash independent of locale, JSON field
        // order and Framework/Core runtime round-trip text formatting changes.
        public static string Fingerprint(CivilWallRuleTemplate value)
        {
            Validate(value);
            var canonical = new StringBuilder("BV.WallRuleTemplate.Canonical/1\n");
            Field(canonical, "SchemaVersion", value.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            Field(canonical, "TemplateId", value.TemplateId); Field(canonical, "Name", value.Name); Field(canonical, "Description", value.Description);
            Field(canonical, "MethodId", value.MethodId); Field(canonical, "AreaMethodId", value.AreaMethodId);
            Field(canonical, "OpeningPolicy", value.OpeningPolicy); Field(canonical, "JoinPolicy", value.JoinPolicy);
            Field(canonical, "Revision", value.Revision.ToString(CultureInfo.InvariantCulture));
            Field(canonical, "AbsoluteToleranceMetres3", BitConverter.DoubleToInt64Bits(value.AbsoluteToleranceMetres3).ToString("x16", CultureInfo.InvariantCulture));
            Field(canonical, "RelativeTolerance", BitConverter.DoubleToInt64Bits(value.RelativeTolerance).ToString("x16", CultureInfo.InvariantCulture));
            Field(canonical, "PriorityCount", value.Priorities.Count.ToString(CultureInfo.InvariantCulture));
            foreach (CivilWallPriority priority in value.Priorities.OrderBy(item => item.ElementUniqueId, StringComparer.Ordinal).ThenBy(item => item.Rank))
            {
                Field(canonical, "ElementUniqueId", priority.ElementUniqueId); Field(canonical, "Rank", priority.Rank.ToString(CultureInfo.InvariantCulture));
            }
            using (SHA256 sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(canonical.ToString())).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public static CivilWallRuleTemplate Load(string path)
        {
            path = TemplatePath(path); CheckLinks(path);
            return FromBytes(ReadBounded(path));
        }

        // Reuse exactly the same strict rule contract for a template embedded
        // inside a versioned project JSON. Unknown fields must not be discarded.
        internal static CivilWallRuleTemplate Parse(byte[] bytes) { return FromBytes(bytes); }

        public static void Save(string path, CivilWallRuleTemplate value)
        {
            CivilWallRuleTemplate candidate = Clone(value);
            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                Serializer().WriteObject(memory, candidate);
                if (memory.Length > MaxBytes) throw new InvalidDataException("规则模板超过 1 MiB。");
                bytes = memory.ToArray();
            }
            path = TemplatePath(path);
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("规则模板保存目录不存在。");
            CheckLinks(directory);
            byte[] previous = null;
            if (File.Exists(path))
            {
                CheckLinks(path); previous = ReadBounded(path);
                CivilWallRuleTemplate saved = FromBytes(previous);
                if (saved.TemplateId != candidate.TemplateId) throw new InvalidDataException("已有文件属于另一规则模板，请另存为新文件。");
                if (saved.Revision > candidate.Revision || (saved.Revision == candidate.Revision && Fingerprint(saved) != Fingerprint(candidate)))
                    throw new InvalidDataException("规则模板内容改变必须提高修订号，且不能覆盖更新的版本。");
            }
            string temporary = Path.Combine(directory, ".bv-wall-rule-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                CheckLinks(directory);
                if (previous != null)
                {
                    CheckLinks(path);
                    if (!previous.SequenceEqual(ReadBounded(path))) throw new InvalidDataException("规则模板文件已变化，请重新载入后再保存。");
                    File.Replace(temporary, path, null);
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static CivilWallRuleTemplate FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("规则模板为空或超过 1 MiB。");
            try
            {
                // DataContractJsonSerializer ignores unknown fields. Validate
                // the JSON shape first, so unsupported options never disappear.
                new UTF8Encoding(false, true).GetString(bytes);
                var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 8, MaxStringContentLength = 16384, MaxArrayLength = 20000, MaxBytesPerRead = 16384, MaxNameTableCharCount = 16384 };
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas))
                {
                    var document = new XmlDocument { XmlResolver = null }; document.Load(reader);
                    Shape(document.DocumentElement, false);
                }
                using (var memory = new MemoryStream(bytes, false))
                {
                    var value = (CivilWallRuleTemplate)Serializer().ReadObject(memory);
                    Validate(value); return value;
                }
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException || error is InvalidDataException) throw;
                throw new InvalidDataException("BV 墙体规则模板 JSON 无效、不受支持或已损坏。", error);
            }
        }

        private static void Shape(XmlElement root, bool priority)
        {
            if (root == null || root.GetAttribute("type") != "object") throw new InvalidDataException("规则模板必须为 JSON 对象。");
            string[] strings = priority ? new[] { "ElementUniqueId" } : new[] { "TemplateId", "Name", "Description", "MethodId", "AreaMethodId", "OpeningPolicy", "JoinPolicy" };
            string[] numbers = priority ? new[] { "Rank" } : new[] { "SchemaVersion", "Revision", "AbsoluteToleranceMetres3", "RelativeTolerance" };
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in root.ChildNodes)
            {
                var item = child as XmlElement;
                if (item == null) throw new InvalidDataException("规则模板包含无效节点。");
                string name = item.Name;
                if (!fields.Add(name)) throw new InvalidDataException("规则模板 JSON 字段重复：" + name);
                if (strings.Contains(name, StringComparer.Ordinal))
                {
                    // The JSON/XML reader represents an ordinary JSON string
                    // without a type attribute; non-string values carry one.
                    string kind = item.GetAttribute("type");
                    if (kind.Length != 0 && kind != "string") throw new InvalidDataException("规则模板字段必须为字符串：" + name);
                }
                else if (numbers.Contains(name, StringComparer.Ordinal))
                {
                    if (item.GetAttribute("type") != "number") throw new InvalidDataException("规则模板字段必须为数字：" + name);
                }
                else if (!priority && name == "Priorities")
                {
                    if (item.GetAttribute("type") != "array") throw new InvalidDataException("模板优先级必须为数组。");
                    foreach (XmlNode node in item.ChildNodes)
                    {
                        var entry = node as XmlElement;
                        if (entry == null || entry.Name != "item") throw new InvalidDataException("模板优先级包含无效节点。");
                        Shape(entry, true);
                    }
                }
                else throw new InvalidDataException("规则模板包含未知字段：" + name);
            }
            if (fields.Count != strings.Length + numbers.Length + (priority ? 0 : 1)) throw new InvalidDataException("规则模板缺少必需字段。");
        }

        private static byte[] ReadBounded(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length <= 0 || file.Length > MaxBytes) throw new InvalidDataException("规则模板为空或超过 1 MiB。");
                using (var memory = new MemoryStream())
                {
                    byte[] buffer = new byte[16384]; int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (memory.Length > MaxBytes - count) throw new InvalidDataException("规则模板超过 1 MiB。");
                        memory.Write(buffer, 0, count);
                    }
                    return memory.ToArray();
                }
            }
        }

        private static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(CivilWallRuleTemplate), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 100000 }); }
        private static void Field(StringBuilder text, string key, string value) { text.Append(key).Append(':').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n'); }
        private static void Required(string value, string label, int maximum) { Text(value, label, maximum, false); }
        private static void Text(string value, string label, int maximum, bool allowEmpty)
        {
            if (value == null || value.Length > maximum || (!allowEmpty && String.IsNullOrWhiteSpace(value)) || value.Any(character => Char.IsControl(character) && (!allowEmpty || (character != '\n' && character != '\r' && character != '\t'))))
                throw new InvalidDataException(label + "无效。");
            try { new UTF8Encoding(false, true).GetByteCount(value); }
            catch (EncoderFallbackException error) { throw new InvalidDataException(label + "包含无效 Unicode。", error); }
        }
        private static void Tolerance(double value, double maximum, string label)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0 || value > maximum) throw new InvalidDataException(label + "必须为正有限值，且不得放宽原有几何校验上限。");
        }
        private static string TemplatePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathRooted(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                !(path.StartsWith("\\\\", StringComparison.Ordinal) || (path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))))
                throw new InvalidDataException("请选择绝对文件路径的 JSON 规则模板。");
            try
            {
                string full = Path.GetFullPath(path), root = Path.GetPathRoot(full);
                if (full.Substring(root.Length).IndexOf(':') >= 0) throw new InvalidDataException("规则模板路径不受支持。");
                return full;
            }
            catch (Exception error)
            {
                if (error is InvalidDataException) throw;
                if (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
                    throw new InvalidDataException("规则模板路径不受支持。", error);
                throw;
            }
        }
        private static void CheckLinks(string path)
        {
            string current = path;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("规则模板及其目录不能使用符号链接或目录联接。");
                string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
    }
}
