using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrightVision.Update
{
    // Offline validation only. Transport, cache ownership and explicit installer
    // launch confirmation belong to the caller. Hashes verify bytes, not authorship.
    internal sealed class PluginReleaseAsset
    {
        internal string Name { get; private set; }
        internal string Url { get; private set; }
        internal string Digest { get; private set; }
        internal long Size { get; private set; }
        internal PluginReleaseAsset(string name, string url, long size, string digest)
        { Name = name; Url = url; Size = size; Digest = digest; }
    }

    internal sealed class PluginRelease
    {
        internal long Id { get; private set; }
        internal string Version { get; private set; }
        internal string Tag { get; private set; }
        internal string Channel { get; private set; }
        internal string PageUrl { get; private set; }
        internal PluginReleaseAsset ManifestAsset { get; private set; }
        internal PluginReleaseAsset InstallerAsset { get; private set; }
        internal PluginRelease(long id, string version, string channel, PluginReleaseAsset manifest, PluginReleaseAsset installer)
        { Id = id; Version = version; Tag = "revit-v" + version; Channel = channel; PageUrl = PluginUpdateCore.ReleasesPageUrl + "/tag/" + Tag; ManifestAsset = manifest; InstallerAsset = installer; }
    }

    internal sealed class PluginUpdateManifest
    {
        internal string Version { get; private set; }
        internal string Channel { get; private set; }
        internal string Tag { get; private set; }
        internal string PageUrl { get; private set; }
        internal PluginReleaseAsset Installer { get; private set; }
        internal string Sha256 { get; private set; }
        internal PluginUpdateManifest(PluginRelease release, string sha256)
        { Version = release.Version; Channel = release.Channel; Tag = release.Tag; PageUrl = release.PageUrl; Installer = release.InstallerAsset; Sha256 = sha256; }
    }

    internal static class PluginUpdateCore
    {
        internal const string ProductId = "BrightVision.RevitExporter";
        internal const string Owner = "queqideliang";
        internal const string Repository = "bright-vision";
        internal const string Publisher = "崛亮视觉";
        internal const string ReleasesPageUrl = "https://github.com/queqideliang/bright-vision/releases";
        internal const int MaxManifestBytes = 64 * 1024;
        internal const int MaxReleaseListBytes = 2 * 1024 * 1024;
        internal const long MaxInstallerBytes = 100L * 1024 * 1024;
        internal const int MaxReleasePages = 5;
        private static readonly Regex VersionPattern = new Regex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string ApiUrl(int page)
        {
            Require(page >= 1 && page <= MaxReleasePages, "Release page is outside the bounded lookup.");
            return "https://api.github.com/repos/" + Owner + "/" + Repository + "/releases?per_page=20&page=" + page.ToString(CultureInfo.InvariantCulture);
        }

        internal static int CompareVersions(string left, string right)
        {
            int[] a = VersionParts(left), b = VersionParts(right);
            for (int i = 0; i < 3; i++) { int result = a[i].CompareTo(b[i]); if (result != 0) return result; }
            return 0;
        }

        internal static List<PluginRelease> ParseReleases(byte[] json, bool includePreview)
        {
            List<object> values = Parse(json, MaxReleaseListBytes) as List<object>;
            Require(values != null && values.Count <= 20, "GitHub release list must contain at most twenty entries.");
            List<PluginRelease> result = new List<PluginRelease>();
            HashSet<long> ids = new HashSet<long>(); HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in values)
            {
                Dictionary<string, object> value = Object(entry);
                object rawTag;
                string tag = value.TryGetValue("tag_name", out rawTag) ? rawTag as string : null;
                if (tag == null || !tag.StartsWith("revit-v", StringComparison.Ordinal) || !VersionPattern.IsMatch(tag.Substring(7))) continue;
                object draft;
                Require(value.TryGetValue("draft", out draft) && draft is bool, "Release draft flag is missing or invalid.");
                if ((bool)draft) continue;
                bool preview = Boolean(value, "prerelease");
                if (preview && !includePreview) continue;
                string version = tag.Substring(7);
                long id = Integer(value, "id", 1, Int64.MaxValue);
                Require(ids.Add(id) && tags.Add(tag), "Duplicate GitHub release identity.");
                Require(Text(value, "html_url") == ReleasesPageUrl + "/tag/" + tag, "Release page is outside the fixed official repository.");
                object rawAssets;
                List<object> assets = value.TryGetValue("assets", out rawAssets) ? rawAssets as List<object> : null;
                Require(assets != null && assets.Count <= 1000, "GitHub release asset list is invalid.");
                string installerName = InstallerName(version);
                PluginReleaseAsset manifest = null, installer = null;
                foreach (object rawAsset in assets)
                {
                    Dictionary<string, object> asset = Object(rawAsset);
                    string name = Text(asset, "name");
                    if (name != "update.json" && name != installerName) continue;
                    PluginReleaseAsset parsed = ReadAsset(asset, tag, name, name == "update.json" ? MaxManifestBytes : MaxInstallerBytes);
                    if (name == "update.json") { Require(manifest == null, "Duplicate update.json asset."); manifest = parsed; }
                    else { Require(installer == null, "Duplicate installer asset."); installer = parsed; }
                }
                Require(manifest != null && installer != null, "Official release lacks its exact manifest or plugin installer.");
                result.Add(new PluginRelease(id, version, preview ? "preview" : "stable", manifest, installer));
            }
            result.Sort(delegate(PluginRelease a, PluginRelease b) { return CompareVersions(b.Version, a.Version); });
            return result;
        }

        // Callers aggregate all bounded API pages before deciding whether the
        // installed version is current. A repeated identical page is harmless;
        // two distinct release identities with the same tag are ambiguous.
        internal static PluginRelease LatestRelease(IEnumerable<PluginRelease> releases)
        {
            if (releases == null) throw new ArgumentNullException("releases");
            PluginRelease latest = null;
            Dictionary<string, PluginRelease> tags = new Dictionary<string, PluginRelease>(StringComparer.Ordinal);
            Dictionary<long, string> ids = new Dictionary<long, string>();
            foreach (PluginRelease release in releases)
            {
                Require(release != null, "Missing release metadata.");
                PluginRelease previous; string tag;
                Require(!tags.TryGetValue(release.Tag, out previous) || SameRelease(previous, release), "Conflicting release tags or metadata across pages.");
                Require(!ids.TryGetValue(release.Id, out tag) || tag == release.Tag, "Conflicting release identifiers across pages.");
                tags[release.Tag] = release; ids[release.Id] = release.Tag;
                if (latest == null || CompareVersions(release.Version, latest.Version) > 0) latest = release;
            }
            return latest;
        }

        internal static PluginUpdateManifest ValidateManifest(byte[] json, PluginRelease release, string currentVersion)
        {
            Require(release != null, "A release must be selected first.");
            Require(CompareVersions(release.Version, currentVersion) > 0, "The installer must be newer than the installed plugin.");
            VerifyAssetBytes(json, release.ManifestAsset, MaxManifestBytes);
            Dictionary<string, object> value = Object(Parse(json, MaxManifestBytes));
            ExactKeys(value, "schemaVersion", "productId", "publisher", "channel", "version", "release", "installer");
            Require(Integer(value, "schemaVersion", 2, 2) == 2 && Text(value, "productId") == ProductId && Text(value, "publisher") == Publisher, "Manifest product, publisher or schema differs from the plugin release contract.");
            Require(Text(value, "version") == release.Version && Text(value, "channel") == release.Channel, "Manifest version or channel differs from its GitHub release.");
            Dictionary<string, object> identity = Object(Field(value, "release"));
            ExactKeys(identity, "owner", "repository", "tag", "url");
            Require(Text(identity, "owner") == Owner && Text(identity, "repository") == Repository && Text(identity, "tag") == release.Tag && Text(identity, "url") == release.PageUrl, "Manifest release identity differs from the official repository.");
            Dictionary<string, object> installer = Object(Field(value, "installer"));
            ExactKeys(installer, "fileName", "size", "sha256", "url");
            string hash = Text(installer, "sha256");
            Require(HashPattern.IsMatch(hash), "Manifest must contain a lowercase SHA-256 checksum.");
            Require(Text(installer, "fileName") == release.InstallerAsset.Name && Text(installer, "url") == release.InstallerAsset.Url && Integer(installer, "size", 2, MaxInstallerBytes) == release.InstallerAsset.Size, "Manifest installer metadata differs from its GitHub asset.");
            Require(release.InstallerAsset.Digest == null || release.InstallerAsset.Digest == "sha256:" + hash, "GitHub installer digest differs from the manifest.");
            return new PluginUpdateManifest(release, hash);
        }

        internal static void VerifyAssetBytes(byte[] bytes, PluginReleaseAsset asset, long maximum)
        {
            Require(asset != null && bytes != null && bytes.LongLength == asset.Size && bytes.LongLength <= maximum && bytes.Length > 0, "Downloaded asset length differs from its release metadata.");
            if (asset.Digest != null) Require(asset.Digest == "sha256:" + Hash(bytes), "Downloaded asset digest differs from its release metadata.");
        }

        internal static void VerifyInstaller(Stream stream, PluginUpdateManifest manifest, CancellationToken token)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            Require(manifest != null && manifest.Installer.Size >= 2 && manifest.Installer.Size <= MaxInstallerBytes, "Installer metadata is invalid.");
            byte[] buffer = new byte[65536]; long total = 0;
            using (SHA256 hash = SHA256.Create())
            {
                for (;;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    for (int i = 0; i < count && total + i < 2; i++) Require(buffer[i] == (total + i == 0 ? 0x4d : 0x5a), "Installer is missing the Windows MZ header.");
                    total += count;
                    Require(total <= manifest.Installer.Size && total <= MaxInstallerBytes, "Installer exceeds its declared size.");
                    hash.TransformBlock(buffer, 0, count, null, 0);
                }
                token.ThrowIfCancellationRequested();
                hash.TransformFinalBlock(new byte[0], 0, 0);
                Require(total == manifest.Installer.Size, "Installer download is incomplete.");
                Require(Hex(hash.Hash) == manifest.Sha256, "Installer SHA-256 verification failed.");
            }
        }

        // initialUrl must be the exact validated asset URL from a parsed release.
        // Signed query parameters are permitted solely on GitHub's release asset
        // host after the first request, never on the repository download URL.
        internal static Uri ValidateDownloadUrl(string value, string initialUrl, bool allowStorageRedirect)
        {
            Uri initial = StrictHttps(initialUrl);
            Require(initial.Host == "github.com" && initialUrl == initial.AbsoluteUri && String.IsNullOrEmpty(initial.Query), "Initial download URL is not canonical.");
            string prefix = ReleasesPageUrl + "/download/";
            Require(initialUrl.StartsWith(prefix, StringComparison.Ordinal), "Initial download URL references a different repository.");
            string[] parts = initialUrl.Substring(prefix.Length).Split('/');
            Require(parts.Length == 2 && parts[0].StartsWith("revit-v", StringComparison.Ordinal) && VersionPattern.IsMatch(parts[0].Substring(7)), "Initial download URL has an invalid plugin tag.");
            string name = parts[1];
            Require(name == "update.json" || name == InstallerName(parts[0].Substring(7)), "Initial download URL has an unexpected asset name.");
            Uri target = StrictHttps(value);
            if (value == initialUrl) return target;
            Require(allowStorageRedirect && target.Host == "release-assets.githubusercontent.com", "Asset redirect left the approved GitHub storage host.");
            Require(target.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal) && target.AbsolutePath.Length > "/github-production-release-asset/".Length, "Asset redirect has an invalid GitHub storage path.");
            return target;
        }

        private static Uri StrictHttps(string value)
        {
            Uri uri;
            Require(!String.IsNullOrEmpty(value) && value.Length <= 16384 && value.IndexOf('\\') < 0 && value.IndexOf('#') < 0 && !HasControl(value) && Uri.TryCreate(value, UriKind.Absolute, out uri), "Update URL is invalid.");
            // Assign again for definite assignment on the legacy C# compiler.
            uri = new Uri(value, UriKind.Absolute);
            Require(uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment), "Update URL must be credential-free HTTPS without a fragment.");
            string rawPath = value.Substring(value.IndexOf("://", StringComparison.Ordinal) + 3);
            int pathStart = rawPath.IndexOf('/'); rawPath = pathStart < 0 ? "" : rawPath.Substring(pathStart);
            int query = rawPath.IndexOf('?'); if (query >= 0) rawPath = rawPath.Substring(0, query);
            Require(rawPath.IndexOf('%') < 0 && rawPath.IndexOf("/./", StringComparison.Ordinal) < 0 && rawPath.IndexOf("/../", StringComparison.Ordinal) < 0 && !rawPath.EndsWith("/..", StringComparison.Ordinal) && !rawPath.EndsWith("/.", StringComparison.Ordinal), "Escaped or relative URL paths are forbidden.");
            return uri;
        }

        private static PluginReleaseAsset ReadAsset(Dictionary<string, object> asset, string tag, string name, long maximum)
        {
            Require(Text(asset, "state") == "uploaded", "Release asset is not uploaded.");
            string url = Text(asset, "browser_download_url"), expected = ReleasesPageUrl + "/download/" + tag + "/" + name;
            Require(url == expected, "Release asset URL differs from the official repository and tag.");
            ValidateDownloadUrl(url, expected, false);
            long size = Integer(asset, "size", name == "update.json" ? 1 : 2, maximum);
            object digest; string digestText = null;
            if (asset.TryGetValue("digest", out digest) && digest != null)
            {
                digestText = digest as string;
                Require(digestText != null && digestText.StartsWith("sha256:", StringComparison.Ordinal) && HashPattern.IsMatch(digestText.Substring(7)), "Release asset digest is invalid.");
            }
            return new PluginReleaseAsset(name, url, size, digestText);
        }

        private static int[] VersionParts(string value)
        {
            Require(value != null && VersionPattern.IsMatch(value), "Version must be numeric x.y.z without leading zeroes.");
            string[] parts = value.Split('.');
            return new int[] { Int32.Parse(parts[0], CultureInfo.InvariantCulture), Int32.Parse(parts[1], CultureInfo.InvariantCulture), Int32.Parse(parts[2], CultureInfo.InvariantCulture) };
        }
        private static string InstallerName(string version) { return "BV-" + version + "-Setup.exe"; }
        private static bool SameRelease(PluginRelease a, PluginRelease b)
        { return a.Id == b.Id && a.Channel == b.Channel && a.PageUrl == b.PageUrl && SameAsset(a.ManifestAsset, b.ManifestAsset) && SameAsset(a.InstallerAsset, b.InstallerAsset); }
        private static bool SameAsset(PluginReleaseAsset a, PluginReleaseAsset b)
        { return a.Name == b.Name && a.Url == b.Url && a.Size == b.Size && a.Digest == b.Digest; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static bool HasControl(string value) { foreach (char c in value) if (c <= 0x20 || c == 0x7f) return true; return false; }
        private static string Hex(byte[] bytes) { StringBuilder text = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return text.ToString(); }
        private static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }
        private static Dictionary<string, object> Object(object value) { Dictionary<string, object> result = value as Dictionary<string, object>; Require(result != null, "Expected a JSON object."); return result; }
        private static object Field(Dictionary<string, object> value, string key) { object result; Require(value.TryGetValue(key, out result), "Required JSON field is missing: " + key); return result; }
        private static string Text(Dictionary<string, object> value, string key) { string result = Field(value, key) as string; Require(result != null, "Expected a JSON string: " + key); return result; }
        private static bool Boolean(Dictionary<string, object> value, string key) { object result = Field(value, key); Require(result is bool, "Expected a JSON boolean: " + key); return (bool)result; }
        private static long Integer(Dictionary<string, object> value, string key, long min, long max)
        { Number number = Field(value, key) as Number; long result; Require(number != null && Int64.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out result), "Expected a nonnegative JSON integer: " + key); result = Int64.Parse(number.Value, CultureInfo.InvariantCulture); Require(result >= min && result <= max, "JSON integer is outside its permitted range: " + key); return result; }
        private static void ExactKeys(Dictionary<string, object> value, params string[] allowed)
        { Require(value.Count == allowed.Length, "Manifest contains missing or unexpected fields."); foreach (string key in allowed) Require(value.ContainsKey(key), "Manifest contains missing or unexpected fields."); }
        private static object Parse(byte[] bytes, int maximum)
        {
            Require(bytes != null && bytes.Length > 0 && bytes.Length <= maximum, "JSON exceeds its bounded size or is empty.");
            string text;
            try { text = Utf8.GetString(bytes); } catch (DecoderFallbackException) { throw new InvalidDataException("Update JSON is not valid UTF-8."); }
            if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
            return new Parser(text).Read();
        }

        private sealed class Number { internal string Value; internal Number(string value) { Value = value; } }

        // Small dependency-free parser shared by Framework 4.5 and .NET 8/10.
        // Duplicate fields, invalid UTF-16, excessive depth and malformed numbers
        // are rejected rather than delegated to runtime-dependent serializers.
        private sealed class Parser
        {
            private readonly string text; private int index; private int nodes;
            internal Parser(string text) { this.text = text; }
            internal object Read() { object value = Value(0); Space(); Require(index == text.Length, "Trailing JSON content."); return value; }
            private void Space() { while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n')) index++; }
            private bool Take(char c) { if (index < text.Length && text[index] == c) { index++; return true; } return false; }
            private object Value(int depth)
            {
                Require(depth <= 32 && ++nodes <= 100000, "JSON structure exceeds its bounded depth or item count.");
                Space(); Require(index < text.Length, "Unexpected end of JSON."); char c = text[index];
                if (c == '"') return String();
                if (Take('{'))
                {
                    Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                    Space(); if (Take('}')) return result;
                    do { Space(); Require(index < text.Length && text[index] == '"', "JSON object key is invalid."); string key = String(); Space(); Require(Take(':'), "JSON object separator is missing."); Require(!result.ContainsKey(key), "Duplicate JSON object key."); result.Add(key, Value(depth + 1)); Space(); if (Take('}')) return result; } while (Take(','));
                    throw new InvalidDataException("JSON object is incomplete.");
                }
                if (Take('['))
                {
                    List<object> result = new List<object>(); Space(); if (Take(']')) return result;
                    do { result.Add(Value(depth + 1)); Space(); if (Take(']')) return result; } while (Take(','));
                    throw new InvalidDataException("JSON array is incomplete.");
                }
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                int start = index; Take('-');
                if (!Take('0')) { Require(index < text.Length && text[index] >= '1' && text[index] <= '9', "JSON number is invalid."); Digits(); }
                if (Take('.')) { int digits = index; Digits(); Require(index > digits, "JSON fraction is incomplete."); }
                if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); int digits = index; Digits(); Require(index > digits, "JSON exponent is incomplete."); }
                return new Number(text.Substring(start, index - start));
            }
            private void Digits() { while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++; }
            private void Literal(string expected) { Require(index + expected.Length <= text.Length && text.Substring(index, expected.Length) == expected, "JSON literal is invalid."); index += expected.Length; }
            private string String()
            {
                Require(Take('"'), "JSON string expected."); StringBuilder result = new StringBuilder();
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c == '"') { string value = result.ToString(); for (int i = 0; i < value.Length; i++) { if (Char.IsHighSurrogate(value[i])) { Require(i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1]), "Unpaired JSON surrogate."); i++; } else Require(!Char.IsLowSurrogate(value[i]), "Unpaired JSON surrogate."); } return value; }
                    Require(c >= 0x20, "Unescaped JSON control character.");
                    if (c == '\\')
                    {
                        Require(index < text.Length, "Incomplete JSON escape."); c = text[index++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': result.Append(c); break;
                            case 'b': result.Append('\b'); break; case 'f': result.Append('\f'); break; case 'n': result.Append('\n'); break; case 'r': result.Append('\r'); break; case 't': result.Append('\t'); break;
                            case 'u': Require(index + 4 <= text.Length, "Incomplete JSON unicode escape."); int scalar; Require(Int32.TryParse(text.Substring(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out scalar), "Invalid JSON unicode escape."); result.Append((char)scalar); index += 4; break;
                            default: throw new InvalidDataException("Invalid JSON escape.");
                        }
                    }
                    else result.Append(c);
                }
                throw new InvalidDataException("Unterminated JSON string.");
            }
        }
    }
}
