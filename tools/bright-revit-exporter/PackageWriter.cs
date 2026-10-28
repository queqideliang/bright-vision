using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BrightVision.Export
{
    internal sealed class RecordSink
    {
        private readonly PackageWriter owner;
        private readonly string folder;
        private readonly string stem;
        private readonly bool array;
        private readonly StringBuilder buffer = new StringBuilder();
        private int records;
        private int chunk;

        internal RecordSink(PackageWriter owner, string folder, string stem, bool array)
        { this.owner = owner; this.folder = folder; this.stem = stem; this.array = array; }

        internal void Add(string id, object value)
        {
            string encoded = Json.Encode(value);
            if (encoded.Length > 24 * 1024 * 1024) throw new InvalidDataException("A model record is too large; split the Revit view.");
            if (records > 0 && (records >= 500 || buffer.Length + encoded.Length > 4 * 1024 * 1024)) Flush();
            if (records++ > 0) buffer.Append(',');
            if (!array) buffer.Append(Json.Encode(id)).Append(':');
            buffer.Append(encoded);
        }

        internal void Flush()
        {
            if (records == 0) return;
            owner.WriteText(folder + "/" + stem + (++chunk).ToString("D5") + ".json", (array ? "[" : "{") + buffer.ToString() + (array ? "]" : "}"));
            buffer.Clear(); records = 0;
        }
    }

    internal sealed class PackageWriter : IDisposable
    {
        private readonly string destination;
        private readonly string temporary;
        private readonly FileStream file;
        private ZipArchive zip;
        private bool completed;
        private bool disposed;
        private readonly HashSet<string> geometryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> materialIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly RecordSink components;
        private readonly RecordSink geometries;
        private readonly RecordSink nodes;
        private readonly RecordSink materials;
        private readonly RecordSink properties;
        private readonly Dictionary<string, LevelRecord> levels = new Dictionary<string, LevelRecord>(StringComparer.Ordinal);
        internal int ComponentCount { get; private set; }
        internal int NodeCount { get; private set; }
        internal int GeometryCount { get { return geometryIds.Count; } }
        internal long TriangleCount { get; private set; }
        internal long ModelJsonBytes { get; private set; }
        internal int TextureCount { get; private set; }
        internal int AssetCount { get; private set; }
        internal long AssetBytes { get; private set; }
        private long jsonBytesWritten;
        internal readonly List<string> Warnings = new List<string>();

        internal PackageWriter(string path)
        {
            destination = Path.GetFullPath(path);
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Choose an existing output folder.");
            temporary = Path.Combine(Path.GetDirectoryName(destination), "." + Path.GetFileName(destination) + ".bright-" + Guid.NewGuid().ToString("N") + ".tmp");
            file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            zip = new ZipArchive(file, ZipArchiveMode.Create, true, new UTF8Encoding(false));
            components = new RecordSink(this, "components", "components", true);
            geometries = new RecordSink(this, "geometries", "g", false);
            nodes = new RecordSink(this, "nodes", "n", false);
            materials = new RecordSink(this, "materials", "m", false);
            properties = new RecordSink(this, "properties", "p", false);
            AddMaterial("default", Fields("color", "#b5c4d5", "opacity", 1.0));
        }

        internal static Dictionary<string, object> Fields(params object[] pairs)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }

        internal static double[] Identity()
        { return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }; }

        internal static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }

        internal string AddGeometry(double[] positions, int[] indices, double[] normals, double[] uv)
        {
            if (positions.Length == 0 || positions.Length % 3 != 0 || indices.Length == 0 || indices.Length % 3 != 0 ||
                normals.Length != positions.Length || uv.Length != positions.Length / 3 * 2) throw new InvalidDataException("Invalid mesh layout.");
            foreach (int index in indices) if (index < 0 || index >= positions.Length / 3) throw new InvalidDataException("Mesh index out of range.");
            string id;
            foreach (double x in positions) Finite(x);
            foreach (double x in normals) Finite(x);
            foreach (double x in uv) Finite(x);
            if (BitConverter.IsLittleEndian)
            {
                // Same byte layout as BinaryWriter, with contiguous copies instead
                // of hundreds of thousands of individual method calls per mesh.
                byte[] bytes = new byte[checked(8 + positions.Length * 8 + indices.Length * 4 + normals.Length * 8 + uv.Length * 8)];
                int offset = 0;
                Buffer.BlockCopy(BitConverter.GetBytes(positions.Length), 0, bytes, offset, 4); offset += 4;
                Buffer.BlockCopy(positions, 0, bytes, offset, positions.Length * 8); offset += positions.Length * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(indices.Length), 0, bytes, offset, 4); offset += 4;
                Buffer.BlockCopy(indices, 0, bytes, offset, indices.Length * 4); offset += indices.Length * 4;
                Buffer.BlockCopy(normals, 0, bytes, offset, normals.Length * 8); offset += normals.Length * 8;
                Buffer.BlockCopy(uv, 0, bytes, offset, uv.Length * 8);
                using (SHA256 hash = SHA256.Create()) id = "g-" + Hex(hash.ComputeHash(bytes));
            }
            else using (MemoryStream bytes = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(bytes))
            {
                writer.Write(positions.Length); foreach (double x in positions) writer.Write(x);
                writer.Write(indices.Length); foreach (int x in indices) writer.Write(x);
                foreach (double x in normals) writer.Write(x);
                foreach (double x in uv) writer.Write(x);
                writer.Flush();
                using (SHA256 hash = SHA256.Create()) id = "g-" + Hex(hash.ComputeHash(bytes.GetBuffer(), 0, checked((int)bytes.Length)));
            }
            if (geometryIds.Add(id))
            {
                if (geometryIds.Count > 1000000) throw new InvalidDataException("Geometry limit exceeded.");
                geometries.Add(id, Fields("type", "MESH", "positions", positions, "indices", indices, "normals", normals, "uv", uv));
            }
            return id;
        }

        private static void Finite(double value)
        { if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Non-finite geometry coordinate."); }

        internal string AddNode(string geometryId, string materialId, double[] matrix, int triangles)
        {
            if (matrix.Length != 16) throw new InvalidDataException("Invalid transform.");
            foreach (double value in matrix) Finite(value);
            if (++NodeCount > 2000000) throw new InvalidDataException("Node limit exceeded.");
            string id = "n-" + NodeCount.ToString("D9");
            nodes.Add(id, Fields("geometry", geometryId, "material", materialId, "matrix", matrix));
            TriangleCount += triangles;
            return id;
        }

        internal void AddMaterial(string id, Dictionary<string, object> value)
        { if (materialIds.Add(id)) materials.Add(id, value); }

        internal void AddProperties(string id, Dictionary<string, object> value) { properties.Add(id, value); }

        internal void AddComponent(Dictionary<string, object> value, string levelName, double? elevation, string category)
        {
            if (++ComponentCount > 500000) throw new InvalidDataException("Component limit exceeded; export a smaller 3D view.");
            components.Add(null, value);
            LevelRecord level;
            if (!levels.TryGetValue(levelName, out level)) { level = new LevelRecord(elevation); levels.Add(levelName, level); }
            else if (!elevation.HasValue || !level.Elevation.HasValue || Math.Abs(level.Elevation.Value - elevation.Value) > 0.000001)
                level.Elevation = null;
            List<object> group;
            if (!level.Elements.TryGetValue(category, out group)) { group = new List<object>(); level.Elements.Add(category, group); }
            group.Add(Fields("id", value["Id"]));
        }

        // A tilted document's level plane does not have one world Z elevation.
        // Basis columns are unitless; translation and elevation use metres.
        internal static double? HorizontalLevelElevation(double[] matrix, double elevation)
        {
            if (matrix.Length != 16) throw new InvalidDataException("Invalid document transform.");
            if (Math.Abs(matrix[2]) > 0.000000001 || Math.Abs(matrix[6]) > 0.000000001 || Math.Abs(matrix[10]) < 0.000000001) return null;
            return matrix[10] * elevation + matrix[14];
        }

        internal void WriteText(string name, string text)
        {
            RegisterEntry(name);
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(text);
            jsonBytesWritten = checked(jsonBytesWritten + Encoding.UTF8.GetByteCount(text));
        }

        private void RegisterEntry(string name)
        {
            if (disposed || completed || zip == null) throw new InvalidOperationException("The model package is closed.");
            if (String.IsNullOrEmpty(name) || name.Length > 220 || name.StartsWith("/", StringComparison.Ordinal) || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0)
                throw new InvalidDataException("Invalid package path.");
            foreach (string part in name.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Invalid package path.");
            if (!entryNames.Add(name)) throw new InvalidDataException("Duplicate package entry: " + name);
        }

        internal void AddJsonFile(string relativePath, object value)
        {
            if (!relativePath.StartsWith("metadata/", StringComparison.Ordinal) || !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Additional JSON must use metadata/*.json.");
            WriteText(relativePath, Json.Encode(value));
        }

        internal void AddFile(string relativePath, string sourcePath)
        {
            bool texture = relativePath.StartsWith("texture/", StringComparison.Ordinal);
            if (!texture && !relativePath.StartsWith("drawings/", StringComparison.Ordinal))
                throw new InvalidDataException("Additional assets must use texture/ or drawings/.");
            string extension = Path.GetExtension(relativePath).ToLowerInvariant();
            if (texture && (relativePath.Substring(8).IndexOf('/') >= 0 || (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".webp")))
                throw new InvalidDataException("Unsupported texture asset.");
            using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length > 512L * 1024 * 1024 || AssetBytes + source.Length > 2147483648L) throw new InvalidDataException("Exported assets exceed the package limit.");
                RegisterEntry(relativePath);
                ZipArchiveEntry entry = zip.CreateEntry(relativePath, texture ? CompressionLevel.Fastest : CompressionLevel.Optimal);
                using (Stream target = entry.Open()) source.CopyTo(target);
                AssetBytes = checked(AssetBytes + source.Length);
                AssetCount++;
                if (texture) TextureCount++;
            }
        }

        internal void Complete(Dictionary<string, object> manifest)
        {
            if (ComponentCount == 0 || NodeCount == 0 || GeometryCount == 0) throw new InvalidDataException("The 3D view has no exported mesh geometry.");
            components.Flush(); geometries.Flush(); nodes.Flush(); materials.Flush(); properties.Flush();
            Dictionary<string, object> levelData = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, LevelRecord> level in levels)
            {
                Dictionary<string, object> groups = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<object>> group in level.Value.Elements) groups.Add(group.Key, group.Value);
                levelData.Add(level.Key, Fields("attribute", level.Value.Elevation.HasValue ? Fields("elevation", level.Value.Elevation.Value) : Fields(), "elements", groups));
            }
            WriteText("LevelInfo.json", Json.Encode(levelData));
            manifest["lengthUnit"] = "m";
            manifest["textureImages"] = TextureCount > 0;
            manifest["textureCount"] = TextureCount;
            manifest["assetCount"] = AssetCount;
            manifest["assetBytes"] = AssetBytes;
            manifest["components"] = ComponentCount;
            manifest["nodes"] = NodeCount;
            manifest["uniqueGeometries"] = GeometryCount;
            manifest["placedTriangles"] = TriangleCount;
            manifest["warnings"] = Warnings;
            ModelJsonBytes = jsonBytesWritten;
            object existingStatistics;
            Dictionary<string, object> statistics = manifest.TryGetValue("exportStatistics", out existingStatistics) ? existingStatistics as Dictionary<string, object> : null;
            if (statistics == null) { statistics = Fields(); manifest["exportStatistics"] = statistics; }
            statistics["modelJsonBytes"] = ModelJsonBytes;
            statistics["modelJsonBytesScope"] = "model-json-excluding-manifest";
            statistics["assetBytes"] = AssetBytes;
            statistics["exportDataBytes"] = checked(ModelJsonBytes + AssetBytes);
            WriteText("manifest.json", Json.Encode(manifest));
            zip.Dispose(); zip = null;
            if (file.Length > 2147483648L) throw new InvalidDataException("Model ZIP exceeds the viewer's 2 GiB input limit; export a smaller view.");
            file.Flush(true); file.Dispose();
            // Both paths are in the same folder. Replace is atomic and retains
            // the old destination on failure; never delete a user's old ZIP first.
            if (File.Exists(destination)) File.Replace(temporary, destination, null, true);
            else File.Move(temporary, destination);
            completed = true;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (zip != null) { zip.Dispose(); zip = null; }
            file.Dispose();
            if (!completed && File.Exists(temporary)) File.Delete(temporary);
        }

        private sealed class LevelRecord
        {
            internal double? Elevation;
            internal readonly Dictionary<string, List<object>> Elements = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            internal LevelRecord(double? elevation) { Elevation = elevation; }
        }
    }
}
