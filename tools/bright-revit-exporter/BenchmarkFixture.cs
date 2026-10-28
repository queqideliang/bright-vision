using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BrightVision.Export;

// Standalone synthetic diagnostics. Not included in the shipped exporter DLL,
// does not reference/load Revit, and does not open any user model.
internal static class BenchmarkFixture
{
    private static readonly List<object> measurements = new List<object>();
    private static long checksum;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Provide output ZIP path, triangle count and instance count.");
            string output = Path.GetFullPath(args[0]);
            int triangles = Int32.Parse(args[1], CultureInfo.InvariantCulture), instances = Int32.Parse(args[2], CultureInfo.InvariantCulture);
            if (triangles < 1 || triangles > 20000 || instances < 2 || instances > 2000) throw new ArgumentOutOfRangeException("Benchmark limits exceeded.");
            double[] positions = new double[triangles * 9], normals = new double[triangles * 9], uv = new double[triangles * 6];
            int[] indices = new int[triangles * 3];
            for (int t = 0; t < triangles; t++) for (int c = 0; c < 3; c++)
            {
                int v = t * 3 + c;
                positions[v * 3] = ((t % 113) + (c == 1 ? 1 : 0)) * 0.3048;
                positions[v * 3 + 1] = ((t / 113) + (c == 2 ? 1 : 0)) * 0.3048;
                normals[v * 3 + 2] = 1; uv[v * 2] = c == 1 ? 1 : 0; uv[v * 2 + 1] = c == 2 ? 1 : 0;
                indices[v] = v;
            }
            // Verify prototype output identity before comparing costs.
            if (Json.Encode(positions) != Typed(positions) || Json.Encode(normals) != Typed(normals) || Json.Encode(uv) != Typed(uv) || Json.Encode(indices) != Typed(indices))
                throw new Exception("Typed numeric prototype changed JSON bytes.");
            using (PackageWriter writer = new PackageWriter(output))
            {
                string geometry = null;
                Measure("current-first-geometry-validation-hash-and-json", 1, delegate { geometry = writer.AddGeometry(positions, indices, normals, uv); });
                if (geometry != BlockCopyHash(positions, indices, normals, uv)) throw new Exception("Block-copy prototype changed the geometry content hash.");
                Measure("current-repeated-geometry-validation-and-hash", instances - 1, delegate { if (writer.AddGeometry(positions, indices, normals, uv) != geometry) throw new Exception("Deduplication failed."); });
                Measure("prototype-blockcopy-validation-and-hash", instances - 1, delegate { if (BlockCopyHash(positions, indices, normals, uv) != geometry) throw new Exception("Hash mismatch."); });
                Measure("current-json-four-numeric-arrays", 3, delegate { checksum += Json.Encode(positions).Length + Json.Encode(normals).Length + Json.Encode(uv).Length + Json.Encode(indices).Length; });
                Measure("prototype-typed-json-four-numeric-arrays", 3, delegate { checksum += Typed(positions).Length + Typed(normals).Length + Typed(uv).Length + Typed(indices).Length; });
                Measure("current-instance-node-and-component-json", 1, delegate {
                    for (int i = 0; i < instances; i++)
                    {
                        double[] matrix = PackageWriter.Identity(); matrix[12] = i * 10;
                        string node = writer.AddNode(geometry, "default", matrix, triangles);
                        writer.AddComponent(PackageWriter.Fields("Id", "synthetic-" + i, "name", "synthetic instance", "type", "Fixture", "Nodes", new object[] { node }, "Property", "", "SymbolProperty", "", "FamilyProperty", ""), "Synthetic", 0, "Fixture");
                    }
                });
                Measure("current-final-json-flush-optimal-zip-and-atomic-commit", 1, delegate { writer.Complete(PackageWriter.Fields("schema", "Bright.Vision.Model/1", "scope", "synthetic-benchmark")); });
                Console.WriteLine(Json.Encode(PackageWriter.Fields("schema", "Bright.Vision.SyntheticBenchmark/1", "runtime", Environment.Version.ToString(),
                    "is64BitProcess", Environment.Is64BitProcess, "triangleCountPerMesh", triangles, "instanceCount", instances,
                    "hashPayloadBytesPerMesh", 8L + triangles * 204L, "uniqueGeometries", writer.GeometryCount,
                    "modelJsonBytes", writer.ModelJsonBytes, "zipBytes", new FileInfo(output).Length, "checksum", checksum,
                    "measurements", measurements, "limitations", "Synthetic managed-code writer only; excludes Revit API, parameter extraction, geometry tessellation and BOS. Prototype timings are not a production speedup claim.")));
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }

    private static void Measure(string name, int iterations, Action action)
    {
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        watch.Stop();
        measurements.Add(PackageWriter.Fields("stage", name, "iterations", iterations, "elapsedMs", watch.Elapsed.TotalMilliseconds,
            "millisecondsPerIteration", watch.Elapsed.TotalMilliseconds / iterations, "gen0Collections", GC.CollectionCount(0) - gen0,
            "gen1Collections", GC.CollectionCount(1) - gen1, "gen2Collections", GC.CollectionCount(2) - gen2));
    }

    private static string Typed(double[] values)
    {
        StringBuilder text = new StringBuilder("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (Double.IsNaN(values[i]) || Double.IsInfinity(values[i])) throw new InvalidDataException("Non-finite JSON number.");
            if (i > 0) text.Append(','); text.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }
    private static string Typed(int[] values)
    {
        StringBuilder text = new StringBuilder("[");
        for (int i = 0; i < values.Length; i++) { if (i > 0) text.Append(','); text.Append(values[i].ToString(CultureInfo.InvariantCulture)); }
        return text.Append(']').ToString();
    }

    private static string BlockCopyHash(double[] positions, int[] indices, double[] normals, double[] uv)
    {
        if (!BitConverter.IsLittleEndian) throw new NotSupportedException("The prototype requires a little-endian platform.");
        foreach (int index in indices) if (index < 0 || index >= positions.Length / 3) throw new InvalidDataException("Mesh index out of range.");
        foreach (double[] array in new double[][] { positions, normals, uv }) foreach (double value in array)
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Non-finite geometry coordinate.");
        byte[] bytes = new byte[checked(8 + positions.Length * 8 + indices.Length * 4 + normals.Length * 8 + uv.Length * 8)];
        int offset = 0;
        Copy(BitConverter.GetBytes(positions.Length), bytes, ref offset); Copy(positions, bytes, ref offset);
        Copy(BitConverter.GetBytes(indices.Length), bytes, ref offset); Copy(indices, bytes, ref offset);
        Copy(normals, bytes, ref offset); Copy(uv, bytes, ref offset);
        using (SHA256 sha = SHA256.Create())
        {
            StringBuilder id = new StringBuilder("g-"); foreach (byte value in sha.ComputeHash(bytes)) id.Append(value.ToString("x2")); return id.ToString();
        }
    }
    private static void Copy(Array input, byte[] output, ref int offset)
    { int count = Buffer.ByteLength(input); Buffer.BlockCopy(input, 0, output, offset, count); offset += count; }
}
