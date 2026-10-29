using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BrightVision.Export;
using BrightVision.Update;

internal static class PluginUpdateCoreFixture
{
    private static int checks;
    private static readonly List<string> passed = new List<string>();
    private static readonly byte[] Installer = new byte[] { 0x4d, 0x5a, 1, 2, 3, 4 };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    private static int Main(string[] args)
    {
        Versions(); Releases(); Manifests(); Downloads(); Urls(); JsonBoundaries();
        if (args.Length > 1) PublishedContract(args[1]);
        File.WriteAllText(Path.Combine(args[0], "report.json"), Json.Encode(O("result", "PASS", "assertionsPassed", checks, "cases", passed, "scope", "Pure update validation DLL; synthetic bytes plus the prepared 1.8.0 contract; no network, Revit, UI, installer launch or installation", "networkUsed", false, "installerStarted", false, "nativeDesktopInput", false)), Utf8);
        return 0;
    }

    private static void Versions()
    {
        Check(PluginUpdateCore.CompareVersions("1.10.0", "1.9.99") > 0, "numeric minor comparison");
        Check(PluginUpdateCore.CompareVersions("1.10.0", "1.10.0") == 0, "equal version comparison");
        Check(PluginUpdateCore.CompareVersions("2.0.0", "10.0.0") < 0, "numeric major comparison");
        Check(PluginUpdateCore.CompareVersions("0.0.0", "0.0.1") < 0, "zero components permitted");
        foreach (string invalid in new string[] { null, "", "1.10", "1.10.0.0", "01.10.0", "1.010.0", "1.10.00", "1.10.0-preview", "v1.10.0", "1.10.0+build", "1.10.0\n", "１.10.0", "1000000000.0.0", "-1.0.0", "1.0.0 " })
        { string copy = invalid; Reject(delegate { PluginUpdateCore.CompareVersions(copy, "1.10.0"); }, "reject noncanonical version " + (copy ?? "null")); }
        Check(PluginUpdateCore.ApiUrl(1) == "https://api.github.com/repos/queqideliang/bright-vision/releases?per_page=20&page=1", "fixed official API URL");
        Check(PluginUpdateCore.ApiUrl(5).EndsWith("page=5"), "fifth bounded API page");
        Reject(delegate { PluginUpdateCore.ApiUrl(0); }, "reject page zero");
        Reject(delegate { PluginUpdateCore.ApiUrl(6); }, "reject unbounded page lookup");
    }

    private static void Releases()
    {
        Dictionary<string, object> preview = Release("1.12.0", true), stable = Release("1.11.0", false);
        Dictionary<string, object> viewer = O("tag_name", "viewer-v99.0.0");
        Dictionary<string, object> draft = O("tag_name", "revit-v99.0.0", "draft", true);
        List<PluginRelease> releases = Parse(preview, viewer, draft, stable);
        Check(releases.Count == 2 && releases[0].Version == "1.12.0" && releases[1].Version == "1.11.0", "preview subscription filters viewer and draft then sorts");
        List<PluginRelease> stableOnly = PluginUpdateCore.ParseReleases(Bytes(new object[] { preview, stable }), false);
        Check(stableOnly.Count == 1 && stableOnly[0].Channel == "stable", "stable subscription excludes preview");
        Check(PluginUpdateCore.LatestRelease(releases).Version == "1.12.0", "latest selection across channels");
        Check(PluginUpdateCore.LatestRelease(new List<PluginRelease>()) == null, "empty latest selection");
        Check(PluginUpdateCore.LatestRelease(Parse(Release("1.8.0", true))).Version == "1.8.0", "latest metadata remains available for older public version");
        Dictionary<string, object> malformedTag = O("tag_name", "revit-v01.99.0", "draft", false);
        Check(Parse(malformedTag).Count == 0, "invalid plugin tag is ineligible");
        ReleaseReject(delegate(Dictionary<string, object> r) { r["html_url"] = "https://github.com/attacker/bright-vision/releases/tag/revit-v1.11.0"; }, "reject release page in another repository");
        ReleaseReject(delegate(Dictionary<string, object> r) { r["draft"] = "false"; }, "reject string draft flag");
        ReleaseReject(delegate(Dictionary<string, object> r) { r.Remove("draft"); }, "reject missing draft flag");
        ReleaseReject(delegate(Dictionary<string, object> r) { r["prerelease"] = null; }, "reject missing preview flag");
        ReleaseReject(delegate(Dictionary<string, object> r) { r["id"] = 0; }, "reject zero release ID");
        ReleaseReject(delegate(Dictionary<string, object> r) { r["assets"] = new object[0]; }, "reject missing manifest and installer");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[0])["state"] = "new"; }, "reject unfinished GitHub asset");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[0])["size"] = PluginUpdateCore.MaxManifestBytes + 1; }, "reject oversized manifest metadata");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[1])["size"] = PluginUpdateCore.MaxInstallerBytes + 1; }, "reject oversized installer metadata");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[1])["browser_download_url"] = "https://github.com/queqideliang/another/releases/download/revit-v1.11.0/BV-1.11.0-Setup.exe"; }, "reject installer URL in another repository");
        ReleaseReject(delegate(Dictionary<string, object> r) { object[] assets = (object[])r["assets"]; r["assets"] = new object[] { assets[0], assets[0], assets[1] }; }, "reject duplicate manifest asset");
        ReleaseReject(delegate(Dictionary<string, object> r) { object[] assets = (object[])r["assets"]; r["assets"] = new object[] { assets[0], assets[1], assets[1] }; }, "reject duplicate installer asset");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[1])["digest"] = "md5:abc"; }, "reject non-SHA256 asset digest");
        ReleaseReject(delegate(Dictionary<string, object> r) { ((Dictionary<string, object>)((object[])r["assets"])[1])["size"] = 2.5; }, "reject fractional installer size");
        Reject(delegate { Parse(stable, stable); }, "reject duplicate releases within a page");
        object[] tooMany = new object[21]; for (int i = 0; i < tooMany.Length; i++) tooMany[i] = viewer;
        Reject(delegate { PluginUpdateCore.ParseReleases(Bytes(tooMany), true); }, "reject overfull release page");
        Dictionary<string, object> conflict = Release("1.11.0", false); conflict["id"] = 999;
        List<PluginRelease> aggregate = Parse(stable); aggregate.AddRange(Parse(conflict));
        Reject(delegate { PluginUpdateCore.LatestRelease(aggregate); }, "reject conflicting tag identities across pages");
        conflict = Release("1.11.0", false); ((Dictionary<string, object>)((object[])conflict["assets"])[1])["size"] = Installer.Length + 1;
        aggregate = Parse(stable); aggregate.AddRange(Parse(conflict));
        Reject(delegate { PluginUpdateCore.LatestRelease(aggregate); }, "reject changed metadata for repeated release across pages");
        aggregate = Parse(stable); aggregate.AddRange(Parse(stable));
        Check(PluginUpdateCore.LatestRelease(aggregate).Version == "1.11.0", "identical repeated release across pages remains usable");
    }

    private static void Manifests()
    {
        byte[] bytes = Bytes(Manifest("1.11.0", false)); PluginRelease release = Parse(Release("1.11.0", false))[0];
        PluginUpdateManifest manifest = PluginUpdateCore.ValidateManifest(bytes, release, "1.10.0");
        Check(manifest.Version == "1.11.0" && manifest.Installer.Name == "BV-1.11.0-Setup.exe" && manifest.Sha256 == Hash(Installer), "valid canonical manifest and matching release metadata");
        Reject(delegate { PluginUpdateCore.ValidateManifest(bytes, release, "1.11.0"); }, "prevent same-version installation");
        Reject(delegate { PluginUpdateCore.ValidateManifest(bytes, release, "1.12.0"); }, "prevent downgrade installation");
        ManifestReject(delegate(Dictionary<string, object> m) { m["productId"] = "local.bim.modelviewer"; }, "reject viewer product manifest");
        ManifestReject(delegate(Dictionary<string, object> m) { m["packageId"] = m["productId"]; m.Remove("productId"); }, "reject invented packageId contract");
        ManifestReject(delegate(Dictionary<string, object> m) { m["schemaVersion"] = 1; }, "reject obsolete schema version");
        ManifestReject(delegate(Dictionary<string, object> m) { m["publisher"] = "Another publisher"; }, "reject publisher mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { m["channel"] = "preview"; }, "reject manifest and release channel mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { m["version"] = "1.12.0"; }, "reject manifest and tag version mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { m["command"] = "setup.exe /silent"; }, "reject command fields");
        ManifestReject(delegate(Dictionary<string, object> m) { m["components"] = O("revitExporterVersion", "1.11.0"); }, "reject viewer-only embedded components");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["release"])["owner"] = "attacker"; }, "reject manifest repository identity mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["release"])["tag"] = "revit-v9.0.0"; }, "reject manifest release tag mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["installer"])["fileName"] = "Bright.Vision-1.11.0-Setup.exe"; }, "reject viewer installer name");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["installer"])["size"] = Installer.Length + 1; }, "reject manifest and GitHub asset length mismatch");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["installer"])["url"] = "https://github.com/queqideliang/bright-vision/releases/download/revit-v1.11.0/BV-1.11.0-Setup.exe?anything"; }, "reject manifest installer query");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["installer"])["sha256"] = Hash(Installer).ToUpperInvariant(); }, "reject noncanonical uppercase digest");
        ManifestReject(delegate(Dictionary<string, object> m) { ((Dictionary<string, object>)m["installer"])["sha256"] = new string('a', 64); }, "reject manifest and GitHub asset digest mismatch");
        Dictionary<string, object> changed = Release("1.11.0", false); ((Dictionary<string, object>)((object[])changed["assets"])[0])["size"] = bytes.Length + 1;
        Reject(delegate { PluginUpdateCore.ValidateManifest(bytes, Parse(changed)[0], "1.10.0"); }, "reject downloaded manifest length mismatch");
        changed = Release("1.11.0", false); ((Dictionary<string, object>)((object[])changed["assets"])[0])["digest"] = "sha256:" + new string('0', 64);
        Reject(delegate { PluginUpdateCore.ValidateManifest(bytes, Parse(changed)[0], "1.10.0"); }, "reject downloaded manifest digest mismatch");
        changed = Release("1.11.0", false); ((Dictionary<string, object>)((object[])changed["assets"])[0])["digest"] = null; ((Dictionary<string, object>)((object[])changed["assets"])[1]).Remove("digest");
        Check(PluginUpdateCore.ValidateManifest(bytes, Parse(changed)[0], "1.10.0").Version == "1.11.0", "older GitHub API without optional asset digests remains compatible");
    }

    private static void Downloads()
    {
        PluginUpdateManifest manifest = PluginUpdateCore.ValidateManifest(Bytes(Manifest("1.11.0", false)), Parse(Release("1.11.0", false))[0], "1.10.0");
        using (MemoryStream stream = new MemoryStream(Installer)) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None);
        Check(true, "valid installer bytes pass length SHA256 and MZ checks");
        using (OneByteStream stream = new OneByteStream(Installer)) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None);
        Check(true, "split MZ header across individual read chunks");
        Reject(delegate { using (MemoryStream stream = new MemoryStream(new byte[] { 0x4d, 0x5a })) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None); }, "reject truncated installer");
        Reject(delegate { using (MemoryStream stream = new MemoryStream(new byte[] { 0x4d, 0x5a, 1, 2, 3, 4, 5 })) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None); }, "reject oversized installer");
        Reject(delegate { using (MemoryStream stream = new MemoryStream(new byte[] { 0x4d, 0x5a, 1, 2, 3, 5 })) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None); }, "reject installer checksum mismatch");
        Reject(delegate { using (MemoryStream stream = new MemoryStream(new byte[] { 0, 0, 1, 2, 3, 4 })) PluginUpdateCore.VerifyInstaller(stream, manifest, CancellationToken.None); }, "reject non-Windows installer prefix");
        bool cancelled = false; using (CancellationTokenSource source = new CancellationTokenSource()) { source.Cancel(); try { using (MemoryStream stream = new MemoryStream(Installer)) PluginUpdateCore.VerifyInstaller(stream, manifest, source.Token); } catch (OperationCanceledException) { cancelled = true; } }
        Check(cancelled, "cancel verification before reading");
    }

    private static void Urls()
    {
        string initial = AssetUrl("1.11.0", "BV-1.11.0-Setup.exe");
        Check(PluginUpdateCore.ValidateDownloadUrl(initial, initial, false).Scheme == "https", "canonical initial download URL");
        string storage = "https://release-assets.githubusercontent.com/github-production-release-asset/123/installer?sig=abc%2Fxyz&expires=123";
        Check(PluginUpdateCore.ValidateDownloadUrl(storage, initial, true).Host == "release-assets.githubusercontent.com", "restricted GitHub asset redirect supports signed query");
        foreach (string url in new string[] { initial.Replace("https:", "http:"), initial + "?x=1", initial + "#fragment", initial.Replace("github.com/", "github.com:444/"), initial.Replace("github.com/", "user:pass@github.com/"), initial.Replace("queqideliang/", "attacker/"), initial.Replace("/revit-v", "/%72evit-v"), initial.Replace("github.com/", "github.com.evil.test/"), "https://objects.githubusercontent.com/github-production-release-asset/123/file", "https://release-assets.githubusercontent.com/anything/file", storage.Replace("https:", "http:"), storage + "#fragment", storage.Replace("/123/", "/%2e%2e/"), storage.Replace("/123/", "/../"), storage.Replace("release-assets.githubusercontent.com/", "attacker@release-assets.githubusercontent.com/"), storage.Replace("/123/", "\\123/") })
        { string copy = url; Reject(delegate { PluginUpdateCore.ValidateDownloadUrl(copy, initial, true); }, "reject unsafe asset URL " + copy); }
        Reject(delegate { PluginUpdateCore.ValidateDownloadUrl(storage, initial, false); }, "reject storage URL before redirect permission");
        Reject(delegate { PluginUpdateCore.ValidateDownloadUrl(initial + "#", initial, true); }, "reject empty URL fragment");
        Reject(delegate { PluginUpdateCore.ValidateDownloadUrl("https://release-assets.githubusercontent.com/github-production-release-asset/123/.", initial, true); }, "reject trailing relative storage path");
        Reject(delegate { PluginUpdateCore.ValidateDownloadUrl(initial, initial.Replace("/bright-vision/", "/other/"), false); }, "reject untrusted initial repository URL");
        Reject(delegate { PluginUpdateCore.ValidateDownloadUrl(initial, initial.Replace("BV-1.11.0-Setup.exe", "script.ps1"), false); }, "reject untrusted initial asset type");
    }

    private static void JsonBoundaries()
    {
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[] trailing"), true); }, "reject trailing JSON");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[{\"tag_name\":\"viewer-v1.0.0\",\"tag_name\":\"revit-v1.11.0\"}]"), true); }, "reject duplicate JSON identity field");
        Reject(delegate { PluginUpdateCore.ParseReleases(new byte[] { 0xff, 0xfe }, true); }, "reject invalid UTF8");
        Reject(delegate { PluginUpdateCore.ParseReleases(new byte[PluginUpdateCore.MaxReleaseListBytes + 1], true); }, "reject release JSON byte limit");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[\"\\ud800\"]"), true); }, "reject unpaired JSON surrogate");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[01]"), true); }, "reject JSON leading-zero number");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[1.]"), true); }, "reject incomplete JSON fraction");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes("[1e]"), true); }, "reject incomplete JSON exponent");
        Reject(delegate { PluginUpdateCore.ParseReleases(Utf8.GetBytes(new string('[', 34) + new string(']', 34)), true); }, "reject excessive JSON depth");
        byte[] bom = new byte[] { 0xef, 0xbb, 0xbf, (byte)'[', (byte)']' };
        Check(PluginUpdateCore.ParseReleases(bom, true).Count == 0, "valid UTF8 BOM supported");
    }

    private static void PublishedContract(string path)
    {
        byte[] sample = File.ReadAllBytes(path);
        Dictionary<string, object> release = Release("1.8.0", true);
        object[] assets = (object[])release["assets"];
        ((Dictionary<string, object>)assets[0])["size"] = sample.Length; ((Dictionary<string, object>)assets[0])["digest"] = "sha256:" + Hash(sample);
        ((Dictionary<string, object>)assets[1])["size"] = 1674752;
        ((Dictionary<string, object>)assets[1])["digest"] = "sha256:73283e37ade3cd9832412fdb6ba2c7e4084e8048eb9be0b9e9376c6f76f6cf53";
        Check(PluginUpdateCore.ValidateManifest(sample, Parse(release)[0], "1.7.0").Version == "1.8.0", "existing prepared 1.8.0 preview manifest remains compatible");
        Reject(delegate { PluginUpdateCore.ValidateManifest(sample, Parse(release)[0], "1.10.0"); }, "existing older public release cannot downgrade 1.10.0");
    }

    private static void ReleaseReject(Action<Dictionary<string, object>> change, string name) { Dictionary<string, object> value = Release("1.11.0", false); change(value); Reject(delegate { Parse(value); }, name); }
    private static void ManifestReject(Action<Dictionary<string, object>> change, string name)
    {
        Dictionary<string, object> manifest = Manifest("1.11.0", false); change(manifest); byte[] bytes = Bytes(manifest);
        Dictionary<string, object> release = Release("1.11.0", false); Dictionary<string, object> asset = (Dictionary<string, object>)((object[])release["assets"])[0]; asset["size"] = bytes.Length; asset["digest"] = "sha256:" + Hash(bytes);
        Reject(delegate { PluginUpdateCore.ValidateManifest(bytes, Parse(release)[0], "1.10.0"); }, name);
    }
    private static List<PluginRelease> Parse(params object[] values) { return PluginUpdateCore.ParseReleases(Bytes(values), true); }
    private static Dictionary<string, object> Release(string version, bool preview)
    {
        byte[] manifest = Bytes(Manifest(version, preview));
        return O("id", Int32.Parse(version.Split('.')[1]) + 1, "tag_name", "revit-v" + version, "draft", false, "prerelease", preview, "html_url", PluginUpdateCore.ReleasesPageUrl + "/tag/revit-v" + version,
            "assets", new object[] { O("name", "update.json", "state", "uploaded", "size", manifest.Length, "digest", "sha256:" + Hash(manifest), "browser_download_url", AssetUrl(version, "update.json")), O("name", "BV-" + version + "-Setup.exe", "state", "uploaded", "size", Installer.Length, "digest", "sha256:" + Hash(Installer), "browser_download_url", AssetUrl(version, "BV-" + version + "-Setup.exe")) });
    }
    private static Dictionary<string, object> Manifest(string version, bool preview)
    {
        return O("schemaVersion", 2, "productId", PluginUpdateCore.ProductId, "publisher", PluginUpdateCore.Publisher, "channel", preview ? "preview" : "stable", "version", version,
            "release", O("owner", PluginUpdateCore.Owner, "repository", PluginUpdateCore.Repository, "tag", "revit-v" + version, "url", PluginUpdateCore.ReleasesPageUrl + "/tag/revit-v" + version),
            "installer", O("fileName", "BV-" + version + "-Setup.exe", "size", Installer.Length, "sha256", Hash(Installer), "url", AssetUrl(version, "BV-" + version + "-Setup.exe")));
    }
    private static string AssetUrl(string version, string file) { return PluginUpdateCore.ReleasesPageUrl + "/download/revit-v" + version + "/" + file; }
    private static Dictionary<string, object> O(params object[] fields) { Dictionary<string, object> result = new Dictionary<string, object>(); for (int i = 0; i < fields.Length; i += 2) result.Add((string)fields[i], fields[i + 1]); return result; }
    private static byte[] Bytes(object value) { return Utf8.GetBytes(Json.Encode(value)); }
    private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; passed.Add(name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
    private sealed class OneByteStream : MemoryStream
    { internal OneByteStream(byte[] bytes) : base(bytes) { } public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(count, 1)); } }
}
