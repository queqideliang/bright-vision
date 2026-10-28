using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;

namespace BrightVision.Export
{
    internal sealed class SourceModelSize
    {
        internal readonly string Status;
        internal readonly long? Bytes;
        internal SourceModelSize(string status, long? bytes) { Status = status; Bytes = bytes; }
    }

    internal sealed class SizeComparison
    {
        internal readonly string Direction;
        internal readonly long? ChangeBytes;
        internal readonly double? ChangePercent;
        internal SizeComparison(string direction, long? bytes, double? percent)
        { Direction = direction; ChangeBytes = bytes; ChangePercent = percent; }
    }

    // No Revit dependency: only reads actual filesystem metadata and computes
    // size comparisons. It never saves a model or treats a missing size as zero.
    internal static class ExportStatistics
    {
        internal static SourceModelSize ReadSavedRvt(string documentPath, bool isCloud)
        {
            if (isCloud) return new SourceModelSize("cloud-or-nonfilesystem", null);
            if (String.IsNullOrWhiteSpace(documentPath)) return new SourceModelSize("unsaved", null);
            try
            {
                if (!Path.IsPathRooted(documentPath) || !String.Equals(Path.GetExtension(documentPath), ".rvt", StringComparison.OrdinalIgnoreCase))
                    return new SourceModelSize("cloud-or-nonfilesystem", null);
                long? size = ReadFileSize(documentPath);
                return new SourceModelSize(size.HasValue ? "available" : "unavailable", size);
            }
            catch (ArgumentException) { return new SourceModelSize("unavailable", null); }
            catch (NotSupportedException) { return new SourceModelSize("unavailable", null); }
            catch (SecurityException) { return new SourceModelSize("unavailable", null); }
        }

        internal static long? ReadFileSize(string filePath)
        {
            try
            {
                FileInfo file = new FileInfo(filePath);
                file.Refresh();
                return file.Exists ? (long?)file.Length : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (SecurityException) { return null; }
        }

        internal static SizeComparison Compare(long? originalBytes, long? finalBytes)
        {
            if ((originalBytes.HasValue && originalBytes.Value < 0) || (finalBytes.HasValue && finalBytes.Value < 0))
                throw new ArgumentOutOfRangeException("File sizes cannot be negative.");
            if (!originalBytes.HasValue || !finalBytes.HasValue) return new SizeComparison("unavailable", null, null);
            long difference = originalBytes.Value - finalBytes.Value;
            long magnitude = Math.Abs(difference);
            double? percent = originalBytes.Value > 0 ? (double?)((double)magnitude * 100.0 / originalBytes.Value) : null;
            return new SizeComparison(difference > 0 ? "reduced" : difference < 0 ? "increased" : "unchanged", magnitude, percent);
        }

        internal static Dictionary<string, object> SourceManifest(SourceModelSize source, bool isModified)
        {
            return PackageWriter.Fields("sourceRvtBytes", source.Bytes.HasValue ? (object)source.Bytes.Value : null,
                "sourceRvtStatus", source.Status, "sourceIsModified", isModified,
                "comparisonScope", "selected-3d-view-and-selected-attachments-vs-last-saved-main-rvt");
        }

        internal static string FormatBytes(long? bytes)
        {
            if (!bytes.HasValue) return "不可用";
            if (bytes.Value < 0) throw new ArgumentOutOfRangeException("bytes");
            if (bytes.Value < 1024) return bytes.Value.ToString("N0", CultureInfo.InvariantCulture) + " 字节";
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
            double value = bytes.Value; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unit] + "（" + bytes.Value.ToString("N0", CultureInfo.InvariantCulture) + " 字节）";
        }

        internal static string Summary(SourceModelSize source, bool isModified, long? zipBytes, long modelJsonBytes, long assetBytes = 0)
        {
            string sourceText = source.Status == "unsaved" ? "未保存，大小不可用"
                : source.Status == "cloud-or-nonfilesystem" ? "云模型或非文件路径，大小不可用"
                : source.Status == "unavailable" ? "本地 RVT 不存在或不可读取，大小不可用" : FormatBytes(source.Bytes);
            string description = "源 RVT（磁盘最后保存版本" + (isModified ? "；模型有未保存修改" : "") + "）：" + sourceText;
            string comparison = ComparisonText(Compare(source.Bytes, zipBytes));
            return description + "\n导出 ZIP（磁盘实际大小）：" + FormatBytes(zipBytes)
                + "\nRVT → ZIP：" + comparison
                + "\n模型 JSON 数据（压缩前，不含 manifest）：" + FormatBytes(modelJsonBytes)
                + (assetBytes > 0 ? "\n贴图与二维附件（压缩前）：" + FormatBytes(assetBytes) : "")
                + "\n导出数据 → ZIP 大小：" + ComparisonText(Compare(checked(modelJsonBytes + assetBytes), zipBytes))
                + "（ZIP 另含 manifest 和目录开销）"
                + "\n以上比较所选三维视图与所选附件的导出包和主 RVT；不是完整 RVT 的无损压缩，链接 RVT 文件大小未计入源大小。";
        }

        private static string ComparisonText(SizeComparison comparison)
        {
            if (!comparison.ChangeBytes.HasValue) return "无法比较（源或输出大小不可用）";
            string direction = comparison.Direction == "reduced" ? "体积缩减" : comparison.Direction == "increased" ? "体积增加" : "体积相同";
            return direction + " " + FormatBytes(comparison.ChangeBytes) + "（"
                + (comparison.ChangePercent.HasValue ? comparison.ChangePercent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "比例不可用：源大小为 0") + "）";
        }
    }
}
