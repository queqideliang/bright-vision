using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;

// A fixed, build-time-compiled data extractor. It does not compile code, start
// other processes, access the network, or load Revit.
internal static class ArchiveExtractor
{
    private const long MaxArchiveBytes = 34359738368L;
    private const long MaxModelBytes = 2147483648L;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string partial = null;
        bool ownsPartial = false;
        try
        {
            if (args.Length != 1 || !Path.IsPathRooted(args[0])) throw new Exception("读取请求无效。");
            string requestPath = Path.GetFullPath(args[0]);
            var requestInfo = new FileInfo(requestPath);
            if (requestInfo.Name != "extract.json" || requestInfo.Length <= 0 || requestInfo.Length > 65536L)
                throw new Exception("读取请求大小无效。");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 8 };
            var request = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(requestPath, Encoding.UTF8));
            string input = (string)request["input"], output = (string)request["output"], kind = (string)request["kind"];
            object indexValue = request["index"];
            if (!(indexValue is int)) throw new Exception("包内文件索引无效。");
            int index = (int)indexValue;
            if (index < 0 || index >= 1000000 || kind != "zip") throw new Exception("模型选择无效。");
            if (!Path.IsPathRooted(input) || !input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(output))
                throw new Exception("模型路径无效。");
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            string expectedOutput = Path.Combine(requestInfo.DirectoryName, "model.zip");
            if (!String.Equals(output, expectedOutput, StringComparison.OrdinalIgnoreCase)) throw new Exception("输出路径无效。");
            EnsureNoLinks(requestInfo.DirectoryName);
            if ((File.GetAttributes(requestPath) & FileAttributes.ReparsePoint) != 0) throw new Exception("读取请求不能使用链接。");
            var inputInfo = new FileInfo(input);
            if (!inputInfo.Exists || inputInfo.Length > MaxArchiveBytes) throw new Exception("模型包大小无效。");
            if (File.Exists(output) || Directory.Exists(output)) throw new Exception("输出文件已经存在。");
            partial = output + ".tmp";
            using (var archive = ZipFile.OpenRead(input))
            {
                if (archive.Entries.Count > 1000000 || index >= archive.Entries.Count) throw new Exception("包内文件索引无效。");
                var entry = archive.Entries[index];
                if (!entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new Exception("模型类型与包内文件不一致。");
                if (entry.Length > MaxModelBytes) throw new Exception("选中的模型超过 2 GiB。");
                using (var source = entry.Open())
                using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsPartial = true;
                    byte[] buffer = new byte[1048576];
                    long written = 0;
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (written > MaxModelBytes - count || written + count > entry.Length) throw new Exception("包内模型超过声明大小。");
                        destination.Write(buffer, 0, count);
                        written += count;
                    }
                    if (written != entry.Length) throw new Exception("包内模型读取不完整。");
                }
                File.Move(partial, output);
                ownsPartial = false;
            }
            return 0;
        }
        catch (Exception error)
        {
            if (ownsPartial && partial != null) { try { File.Delete(partial); } catch { } }
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void EnsureNoLinks(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("输出目录不能使用目录联接或符号链接。");
    }
}
