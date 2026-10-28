using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using BrightVision.Export;
using BrightVision.Revit;

internal static class ExportFixture
{
    private static Dictionary<string, object> Component(string id, string node, string property)
    {
        return PackageWriter.Fields("Id", id, "name", "合成三角形 " + id, "type", "测试构件", "BuildInCategory", "-2000011",
            "familyName", "Fixture", "familySymbol", "Triangle", "matrix", PackageWriter.Identity(), "Nodes", new object[] { node },
            "Property", property, "SymbolProperty", "", "FamilyProperty", "",
            "QuantitySnapshot", QuantitySnapshot.Create("fixture-unique-" + id, id, "fixture-project", "root",
                id == "fixture-1" ? QuantitySnapshot.FromInternalValue(10, 1) : QuantitySnapshot.Missing(),
                QuantitySnapshot.FromInternalValue(id == "fixture-1" ? 100 : 25, 2),
                QuantitySnapshot.FromInternalValue(id == "fixture-1" ? 0 : 8, 3)));
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Provide the fixture ZIP path.");
            string path = Path.GetFullPath(args[0]);
            VerifyLevelTransforms();
            VerifyComparisons();
            VerifyNumericEncoding();
            VerifyQuantitySnapshot();
            string sourcePath = Path.Combine(Path.GetDirectoryName(path), "fixture-source.rvt");
            File.WriteAllBytes(sourcePath, new byte[4096]);
            SourceModelSize sourceSize = ExportStatistics.ReadSavedRvt(sourcePath, false);
            long modelJsonBytes;
            using (PackageWriter writer = new PackageWriter(path))
            {
                double[] points = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
                int[] indices = { 0, 1, 2 };
                double[] normals = { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
                double[] uv = { 0, 0, 1, 0, 0, 1 };
                string geometry = writer.AddGeometry(points, indices, normals, uv);
                if (writer.AddGeometry(points, indices, normals, uv) != geometry || writer.GeometryCount != 1) throw new Exception("Deduplication failed.");
                writer.AddMaterial("fixture-blue", PackageWriter.Fields("color", "#1688ff", "opacity", 1));
                double[] matrix = PackageWriter.Identity(); matrix[12] = 10; matrix[13] = 20; matrix[14] = 3;
                string node = writer.AddNode(geometry, "fixture-blue", matrix, 1);
                writer.AddProperties("p-first", PackageWriter.Fields("来源标识", PackageWriter.Fields("UniqueId", "fixture-unique-1", "DocumentScope", "fixture-project"),
                    "参数", PackageWriter.Fields("说明", "中文、引号 \" 与换行\n正确转义", "数值", 42.5)));
                writer.AddComponent(Component("fixture-1", node, "p-first"), "一层", 3.0, "测试构件");
                double[] mirrored = PackageWriter.Identity(); mirrored[0] = -1; mirrored[12] = 15; mirrored[13] = 20; mirrored[14] = 3;
                string second = writer.AddNode(geometry, "fixture-blue", mirrored, 1);
                writer.AddProperties("p-second", PackageWriter.Fields("来源标识", PackageWriter.Fields("UniqueId", "fixture-unique-2", "DocumentScope", "fixture-project")));
                writer.AddComponent(Component("fixture-2", second, "p-second"), "一层", 3.0, "测试构件");
                VerifyGeometryHash(writer, geometry, points, indices, normals, uv);
                writer.Complete(PackageWriter.Fields("schema", "Bright.Vision.Model/1", "exporterVersion", "1.4.0", "sourceDocument", "Synthetic fixture", "linkedModels", true,
                    "scope", "synthetic-test-data", "exportStatistics", ExportStatistics.SourceManifest(sourceSize, true)));
                modelJsonBytes = writer.ModelJsonBytes;
            }
            VerifySourceSizes(sourcePath);
            VerifyWrittenStatistics(path, sourceSize, modelJsonBytes);
            VerifyBrandPixels(Path.GetDirectoryName(path));
            WriteLevelFixture(Path.Combine(Path.GetDirectoryName(path), "export-level-fixture.zip"));
            VerifyAssets(Path.Combine(Path.GetDirectoryName(path), "export-assets-fixture.zip"));
            // A failed export must not replace an existing file or leave its temp.
            string retained = path + ".existing"; File.WriteAllText(retained, "ORIGINAL");
            bool rejected = false;
            try { using (PackageWriter writer = new PackageWriter(retained)) writer.Complete(PackageWriter.Fields()); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected || File.ReadAllText(retained) != "ORIGINAL") throw new Exception("Failed-export preservation failed.");
            string[] leftovers = Directory.GetFiles(Path.GetDirectoryName(path), "." + Path.GetFileName(retained) + ".bright-*.tmp");
            if (leftovers.Length != 0) throw new Exception("Failed-export temporary files remain.");
            Console.WriteLine("PASS: fixture ZIP, shared geometry, mirrored node, metadata, document level transforms, mixed elevations, actual source/ZIP sizes, reduction/increase/unknown statistics, original 16/32px brand pixels, numeric/hash equivalence, SI quantity snapshots and non-finite/missing/zero statuses, texture/drawing/metadata assets, path and duplicate rejection, failed-export preservation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }

    private static void VerifyQuantitySnapshot()
    {
        if (Math.Abs(QuantitySnapshot.FromInternalValue(1, 1).Value.Value - 0.3048) > 1e-15) throw new Exception("Length did not convert feet to metres.");
        if (Math.Abs(QuantitySnapshot.FromInternalValue(1, 2).Value.Value - 0.09290304) > 1e-15) throw new Exception("Area did not use the squared unit scale.");
        if (Math.Abs(QuantitySnapshot.FromInternalValue(1, 3).Value.Value - 0.028316846592) > 1e-15) throw new Exception("Volume did not use the cubed unit scale.");
        if (QuantitySnapshot.FromInternalValue(0, 3).Status != "available" || QuantitySnapshot.FromInternalValue(0, 3).Value != 0) throw new Exception("Zero quantity was lost.");
        foreach (double value in new double[] { -1, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            if (QuantitySnapshot.FromInternalValue(value, 1).Status != "invalid" || QuantitySnapshot.FromInternalValue(value, 1).Value.HasValue) throw new Exception("Invalid quantity was accepted.");
        string json = Json.Encode(QuantitySnapshot.Create("unique", "element", "document", "root", QuantitySnapshot.Missing(), QuantitySnapshot.Unavailable(), QuantitySnapshot.Invalid()));
        if (!json.Contains("\"value\":null") || json.Contains("\"value\":0") || !json.Contains("\"unit\":\"m3\"") || !json.Contains("\"parameter\":\"HOST_AREA_COMPUTED\"")) throw new Exception("Snapshot status or provenance serialization failed.");
    }

    private static void VerifyNumericEncoding()
    {
        double[] numbers = { 0, -0.0, -1.25, Double.Epsilon, Double.MaxValue, Double.MinValue, 1.2345678901234567 };
        object[] boxed = new object[numbers.Length];
        for (int i = 0; i < numbers.Length; i++) boxed[i] = numbers[i];
        if (Json.Encode(numbers) != Json.Encode(boxed)) throw new Exception("Typed double encoding changed round-trip values.");
        int[] integers = { Int32.MinValue, -1, 0, Int32.MaxValue };
        if (Json.Encode(integers) != Json.Encode(new object[] { Int32.MinValue, -1, 0, Int32.MaxValue })) throw new Exception("Typed integer encoding changed.");
        bool rejected = false;
        try { Json.Encode(new double[] { Double.NaN }); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Typed arrays accepted a non-finite value.");
    }

    private static void VerifyGeometryHash(PackageWriter package, string actual, double[] positions, int[] indices, double[] normals, double[] uv)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter binary = new BinaryWriter(stream))
        {
            binary.Write(positions.Length); foreach (double value in positions) binary.Write(value);
            binary.Write(indices.Length); foreach (int value in indices) binary.Write(value);
            foreach (double value in normals) binary.Write(value);
            foreach (double value in uv) binary.Write(value);
            binary.Flush();
            using (SHA256 hash = SHA256.Create())
            {
                string expected = "g-" + BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new Exception("Optimized mesh hashing changed the original identity.");
            }
        }
    }

    private static void VerifyAssets(string path)
    {
        string texture = Path.ChangeExtension(path, ".png");
        File.WriteAllBytes(texture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="));
        string drawing = Path.ChangeExtension(path, ".dwg");
        File.WriteAllText(drawing, "SYNTHETIC ATTACHMENT - NOT A REAL DRAWING");
        using (PackageWriter package = new PackageWriter(path))
        {
            string mesh = package.AddGeometry(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new int[] { 0, 1, 2 },
                new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new double[] { 0, 0, 1, 0, 0, 1 });
            package.AddFile("texture/fixture.png", texture);
            package.AddFile("drawings/fixture.dwg", drawing);
            package.AddMaterial("texture-fixture", PackageWriter.Fields("color", "#ffffff", "map", "texture/fixture.png"));
            string node = package.AddNode(mesh, "texture-fixture", PackageWriter.Identity(), 1);
            package.AddComponent(Component("asset-fixture", node, ""), "Fixture", 0, "Fixture");
            package.AddJsonFile("metadata/systems.json", PackageWriter.Fields("scope", "synthetic-test-data", "groups", new object[0]));
            foreach (string invalid in new string[] { "texture/../escape.png", "texture/fixture.png", "texture/FIXTURE.png", "texture/sub/image.png", "../escape.png", "texture/bad.gif", "drawings/../escape.dwg" })
            {
                bool rejected = false;
                try { package.AddFile(invalid, texture); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Unsafe or duplicate asset name accepted: " + invalid);
            }
            if (package.TextureCount != 1 || package.AssetCount != 2 || package.AssetBytes != new FileInfo(texture).Length + new FileInfo(drawing).Length)
                throw new Exception("Asset counts or bytes are incorrect.");
            package.Complete(PackageWriter.Fields("schema", "Bright.Vision.Model/1", "scope", "synthetic-test-data"));
        }
        using (FileStream input = File.OpenRead(path))
        using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read))
        {
            if (archive.GetEntry("texture/fixture.png").Length != new FileInfo(texture).Length || archive.GetEntry("drawings/fixture.dwg").Length != new FileInfo(drawing).Length || archive.GetEntry("metadata/systems.json") == null)
                throw new Exception("Assets missing from final model package.");
            using (StreamReader manifest = new StreamReader(archive.GetEntry("manifest.json").Open()))
                if (!manifest.ReadToEnd().Contains("\"textureImages\":true")) throw new Exception("Texture presence is not recorded accurately.");
        }
    }

    private static void VerifyComparisons()
    {
        SizeComparison reduced = ExportStatistics.Compare(1000, 250);
        if (reduced.Direction != "reduced" || reduced.ChangeBytes != 750 || reduced.ChangePercent != 75) throw new Exception("Reduction calculation failed.");
        SizeComparison increased = ExportStatistics.Compare(1000, 1250);
        if (increased.Direction != "increased" || increased.ChangeBytes != 250 || increased.ChangePercent != 25) throw new Exception("Increase calculation failed.");
        SizeComparison same = ExportStatistics.Compare(1000, 1000);
        if (same.Direction != "unchanged" || same.ChangeBytes != 0 || same.ChangePercent != 0) throw new Exception("Unchanged calculation failed.");
        SizeComparison unknown = ExportStatistics.Compare(null, 250);
        if (unknown.Direction != "unavailable" || unknown.ChangeBytes.HasValue || unknown.ChangePercent.HasValue) throw new Exception("Missing source was treated as zero.");
        if (ExportStatistics.Compare(1000, null).ChangeBytes.HasValue) throw new Exception("Missing ZIP was treated as zero.");
        SizeComparison zero = ExportStatistics.Compare(0, 250);
        if (zero.Direction != "increased" || zero.ChangeBytes != 250 || zero.ChangePercent.HasValue) throw new Exception("Zero-size percentage is invalid.");
        if (ExportStatistics.Compare(long.MaxValue, 0).ChangeBytes != long.MaxValue || ExportStatistics.Compare(0, long.MaxValue).ChangeBytes != long.MaxValue)
            throw new Exception("Large file difference overflowed.");
        bool rejected = false; try { ExportStatistics.Compare(-1, 1); } catch (ArgumentOutOfRangeException) { rejected = true; }
        if (!rejected) throw new Exception("Negative size was accepted.");
        string larger = ExportStatistics.Summary(new SourceModelSize("available", 1000), true, 1250, 800);
        if (!larger.Contains("体积增加") || !larger.Contains("25.00%") || !larger.Contains("模型有未保存修改") || !larger.Contains("不是完整 RVT 的无损压缩"))
            throw new Exception("Completion summary semantics failed.");
        if (ExportStatistics.FormatBytes(1572864) != "1.50 MiB（1,572,864 字节）") throw new Exception("Byte formatting failed.");
    }

    private static void VerifySourceSizes(string path)
    {
        SourceModelSize saved = ExportStatistics.ReadSavedRvt(path, false);
        if (saved.Status != "available" || saved.Bytes != 4096) throw new Exception("Actual saved source size failed.");
        if (ExportStatistics.ReadSavedRvt("", false).Status != "unsaved") throw new Exception("Unsaved source status failed.");
        if (ExportStatistics.ReadSavedRvt(path, true).Bytes.HasValue || ExportStatistics.ReadSavedRvt(path, true).Status != "cloud-or-nonfilesystem") throw new Exception("Cloud source status failed.");
        if (ExportStatistics.ReadSavedRvt("Autodesk Docs://project/model.rvt", false).Bytes.HasValue) throw new Exception("Cloud URI was measured as a file.");
        if (ExportStatistics.ReadSavedRvt(path + ".missing.rvt", false).Status != "unavailable") throw new Exception("Missing source status failed.");
        if (ExportStatistics.ReadFileSize(path + ".missing").HasValue) throw new Exception("Missing file was assigned a zero length.");
        if (new FileInfo(path).Length != 4096) throw new Exception("Source file was modified by size measurement.");
    }

    private static void VerifyWrittenStatistics(string path, SourceModelSize source, long modelJsonBytes)
    {
        long expected = 0;
        using (FileStream file = File.OpenRead(path))
        using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Read))
            foreach (ZipArchiveEntry entry in zip.Entries) if (entry.FullName != "manifest.json") expected += entry.Length;
        if (expected != modelJsonBytes || expected <= 0) throw new Exception("Uncompressed UTF-8 model JSON byte count failed.");
        long? zipBytes = ExportStatistics.ReadFileSize(path);
        if (zipBytes != new FileInfo(path).Length || !zipBytes.HasValue || zipBytes.Value <= 0) throw new Exception("Actual final ZIP size failed.");
        File.WriteAllText(path + ".stats.txt", ExportStatistics.Summary(source, true, zipBytes, modelJsonBytes));
    }

    private static void VerifyBrandPixels(string directory)
    {
        foreach (int size in new int[] { 16, 32 })
        {
            byte[] pixels = BrandPixels.Create(size);
            if (pixels.Length != size * size * 4) throw new Exception("Brand icon dimensions failed.");
            bool transparent = false, opaque = false, cyan = false, green = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                transparent |= pixels[i + 3] == 0; opaque |= pixels[i + 3] == 255;
                cyan |= pixels[i] == 215 && pixels[i + 1] == 219 && pixels[i + 2] == 64;
                green |= pixels[i] == 163 && pixels[i + 1] == 228 && pixels[i + 2] == 68;
            }
            if (!transparent || !opaque || !cyan || !green) throw new Exception("Original B/V mark lost its colors or transparency.");
            File.WriteAllBytes(Path.Combine(directory, "brand-icon-" + size + ".bgra"), pixels);
        }
    }

    private static void VerifyLevelTransforms()
    {
        double[] matrix = PackageWriter.Identity(); matrix[14] = 2;
        if (PackageWriter.HorizontalLevelElevation(matrix, 3) != 5) throw new Exception("Linked level translation failed.");
        matrix[0] = -1;
        if (PackageWriter.HorizontalLevelElevation(matrix, 3) != 5) throw new Exception("Mirrored horizontal level failed.");
        matrix[10] = -1;
        if (PackageWriter.HorizontalLevelElevation(matrix, 3) != -1) throw new Exception("Inverted horizontal level failed.");
        matrix[2] = 0.5; matrix[10] = Math.Sqrt(0.75);
        if (PackageWriter.HorizontalLevelElevation(matrix, 3).HasValue) throw new Exception("Tilted level claimed a single world elevation.");
    }

    private static void WriteLevelFixture(string path)
    {
        using (PackageWriter writer = new PackageWriter(path))
        {
            string geometry = writer.AddGeometry(new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new int[] { 0, 1, 2 },
                new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new double[] { 0, 0, 1, 0, 0, 1 });
            string node = writer.AddNode(geometry, "default", PackageWriter.Identity(), 1);
            writer.AddComponent(Component("level-1", node, ""), "Multi-placement", 3, "Fixture");
            writer.AddComponent(Component("level-2", node, ""), "Multi-placement", 6, "Fixture");
            writer.AddComponent(Component("level-3", node, ""), "Multi-placement", 3, "Fixture");
            writer.AddComponent(Component("level-4", node, ""), "Tilted", null, "Fixture");
            writer.AddComponent(Component("level-5", node, ""), "Stable", 3, "Fixture");
            writer.Complete(PackageWriter.Fields("schema", "Bright.Vision.Model/1", "scope", "synthetic-level-test-data"));
        }
    }
}
