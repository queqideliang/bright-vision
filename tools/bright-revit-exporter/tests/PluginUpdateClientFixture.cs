// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BrightVision.Export;
using BrightVision.Update;

internal static class PluginUpdateClientFixture
{
    private static int checks;
    private static readonly List<string> cases = new List<string>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly byte[] Installer = new byte[] { 0x4d, 0x5a, 1, 2, 3, 4 };
    private static string output;
    private static string junction;

    private static int Main(string[] args)
    {
        output = args[0]; junction = args[1];
        Run().GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "report.json"), Json.Encode(O("result", "PASS", "assertionsPassed", checks, "cases", cases, "scope", "Fake anonymous HTTP transport and workspace cache only; no real HTTP, Revit, desktop, browser, installer launch or installation", "networkUsed", false, "installerStarted", false, "nativeDesktopInput", false)), Utf8);
        return 0;
    }

    private static async Task Run()
    {
        await Checks(); await Redirects(); await Downloads();
    }

    private static async Task Checks()
    {
        Bundle old = new Bundle("1.8.0", true, Installer), newer = new Bundle("1.12.0", false, Installer);
        FakeTransport fake = new FakeTransport();
        fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(old.Release), Next(1)));
        fake.Add(PluginUpdateCore.ApiUrl(2), JsonResponse(Page(newer.Release), null));
        fake.Add(newer.ManifestUrl, Data(newer.ManifestBytes));
        using (PluginUpdateClient client = Client(fake, "paged"))
        {
            PluginUpdateCheck check = await client.CheckAsync("1.10.0", true, CancellationToken.None);
            Check(check.Latest.Version == "1.12.0" && check.Available.Version == "1.12.0" && check.CurrentVersion == "1.10.0", "multiple API pages select newest plugin and verify manifest");
            Check(fake.Calls.Count == 3 && fake.Empty, "pagination only requests fixed API pages then exact manifest asset");
        }
        Check(fake.Disposed, "client disposes its fake transport");

        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(old.Release), null));
        using (PluginUpdateClient client = Client(fake, "old"))
        {
            PluginUpdateCheck check = await client.CheckAsync("1.10.0", true, CancellationToken.None);
            Check(check.Latest.Version == "1.8.0" && check.Available == null && fake.Calls.Count == 1, "older public latest remains visible without downloading a downgrade manifest");
        }
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(newer.Release), null)); fake.Add(newer.ManifestUrl, Data(newer.ManifestBytes));
        using (PluginUpdateClient client = Client(fake, "new"))
        {
            PluginUpdateCheck check = await client.CheckAsync("1.10.0", false, CancellationToken.None);
            Check(check.Available != null && check.Available.Channel == "stable", "stable subscription accepts verified newer stable plugin release");
        }
        Bundle preview = new Bundle("1.13.0", true, Installer);
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(preview.Release), null));
        using (PluginUpdateClient client = Client(fake, "stable-filter"))
        {
            PluginUpdateCheck check = await client.CheckAsync("1.10.0", false, CancellationToken.None);
            Check(check.Latest == null && check.Available == null && fake.Calls.Count == 1, "stable subscription never requests preview manifest");
        }
        fake = new FakeTransport();
        for (int page = 1; page <= 5; page++) fake.Add(PluginUpdateCore.ApiUrl(page), JsonResponse(ViewerPage(), Next(page)));
        await RejectCheck(fake, "incomplete", "reject five pages that still advertise another page", delegate(Exception error) { return error is InvalidDataException; });
        Check(fake.Calls.Count == 5 && fake.Empty, "incomplete pagination never fetches an unbounded sixth page");

        foreach (int status in new int[] { 403, 429 })
        {
            fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), Response(status, new byte[0], null, null, null));
            await RejectCheck(fake, "status-" + status, "reject GitHub rate limit HTTP " + status, delegate(Exception error) { return error is InvalidOperationException; });
        }
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), Response(302, new byte[0], "https://api.github.com/repos/attacker/another/releases", null, null));
        await RejectCheck(fake, "api-redirect", "API redirects are refused", delegate(Exception error) { return error is InvalidOperationException; });
        Check(fake.Calls.Count == 1, "API redirect never reaches destination transport");

        fake = new FakeTransport(); PluginHttpRead truncated = Data(Page()); truncated.ContentLength = 200;
        fake.Add(PluginUpdateCore.ApiUrl(1), truncated);
        await RejectCheck(fake, "truncated-api", "valid JSON with truncated Content-Length cannot claim current version is latest", delegate(Exception error) { return error is InvalidDataException; });
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(), "<https://api.github.com/repos/attacker/another/releases?per_page=20&page=2>; rel=\"next\""));
        await RejectCheck(fake, "other-pagination", "reject pagination into another repository", delegate(Exception error) { return error is InvalidDataException; });
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(), Next(2)));
        await RejectCheck(fake, "skipped-pagination", "reject skipped pagination sequence", delegate(Exception error) { return error is InvalidDataException; });
        fake = new FakeTransport(); fake.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(newer.Release), null));
        byte[] changedManifest = (byte[])newer.ManifestBytes.Clone(); changedManifest[changedManifest.Length - 1] = (byte)' ';
        fake.Add(newer.ManifestUrl, Data(changedManifest));
        await RejectCheck(fake, "changed-manifest", "reject manifest bytes that differ from GitHub asset digest", delegate(Exception error) { return error is InvalidDataException; });

        fake = new FakeTransport();
        using (CancellationTokenSource source = new CancellationTokenSource())
        using (PluginUpdateClient client = Client(fake, "cancel-check"))
        {
            source.Cancel(); await RejectAsync(delegate { return client.CheckAsync("1.10.0", true, source.Token); }, "cancel check before HTTP request", delegate(Exception error) { return error is OperationCanceledException; });
            Check(fake.Calls.Count == 0, "cancelled check sends no request");
        }
    }

    private static async Task Redirects()
    {
        Bundle bundle = new Bundle("1.11.0", false, Installer);
        string storage = "https://release-assets.githubusercontent.com/github-production-release-asset/123/manifest?sig=abc%2Fxyz";
        FakeTransport fake = CheckTransport(bundle);
        fake.Add(bundle.ManifestUrl, Response(302, new byte[0], storage, null, null)); fake.Add(storage, Data(bundle.ManifestBytes));
        using (PluginUpdateClient client = Client(fake, "allowed-redirect"))
        {
            PluginUpdateCheck check = await client.CheckAsync("1.10.0", false, CancellationToken.None);
            Check(check.Available != null && fake.Empty, "manifest follows approved GitHub release asset storage redirect");
        }
        foreach (string location in new string[] { "https://evil.test/update.json", "https://objects.githubusercontent.com/github-production-release-asset/123/file", "http://release-assets.githubusercontent.com/github-production-release-asset/123/file", "https://release-assets.githubusercontent.com/other/file", "https://release-assets.githubusercontent.com/github-production-release-asset/123/../file", "https://release-assets.githubusercontent.com/github-production-release-asset/123/%2e%2e/file", "https://release-assets.githubusercontent.com/github-production-release-asset/123/file#", "/queqideliang/bright-vision/releases/download/revit-v1.11.0/update.json" })
        {
            fake = CheckTransport(bundle); fake.Add(bundle.ManifestUrl, Response(302, new byte[0], location, null, null));
            await RejectCheck(fake, "bad-redirect-" + Guid.NewGuid().ToString("N"), "reject unsafe raw redirect " + location, delegate(Exception error) { return error is InvalidDataException; });
            Check(fake.Calls.Count == 2, "unsafe redirect is refused before destination HTTP request");
        }
        fake = CheckTransport(bundle);
        string cursor = bundle.ManifestUrl;
        for (int i = 0; i < 6; i++) { string next = storage + "&hop=" + i; fake.Add(cursor, Response(302, new byte[0], next, null, null)); cursor = next; }
        await RejectCheck(fake, "redirect-limit", "reject more than five asset redirects", delegate(Exception error) { return error is InvalidDataException; });
        Check(fake.Calls.Count == 7 && fake.Empty, "redirect bound stops before seventh asset response");
    }

    private static async Task Downloads()
    {
        Bundle bundle = new Bundle("1.11.0", false, Installer);
        FakeTransport fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, Data(Installer));
        PluginInstallerDownload download;
        List<long> progress = new List<long>();
        string successRoot = Cache("successful-download");
        using (PluginUpdateClient client = new PluginUpdateClient(fake, successRoot))
        {
            download = await client.DownloadAsync(bundle.Validated(), new CallbackProgress(delegate(long value) { progress.Add(value); }), CancellationToken.None);
            Check(File.Exists(download.Path) && Path.GetFileName(download.Path) == "BV-1.11.0-Setup.exe", "download stages exact installer basename in its own cache directory");
            Check(Hash(File.ReadAllBytes(download.Path)) == Hash(Installer), "downloaded bytes match manifest SHA256");
            Check(progress.Count > 0 && progress[progress.Count - 1] == Installer.Length, "download reports received byte count");
            PluginUpdateClient.VerifyDownloaded(download); Check(true, "staged installer revalidation succeeds");
            Check(NoPartial(successRoot), "successful download leaves no partial file");
        }
        byte[] changed = (byte[])Installer.Clone(); changed[changed.Length - 1] ^= 1; File.WriteAllBytes(download.Path, changed);
        Reject(delegate { PluginUpdateClient.VerifyDownloaded(download); }, "post-download modification fails fresh verification");

        fake = new FakeTransport(); PluginHttpRead wrongLength = Data(Installer); wrongLength.ContentLength = Installer.Length + 1; fake.Add(bundle.InstallerUrl, wrongLength);
        await RejectDownload(fake, bundle, "wrong-length-header", "reject installer Content-Length mismatch", CancellationToken.None, null);
        fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, Response(200, new byte[] { 0x4d, 0x5a }, null, null, null));
        await RejectDownload(fake, bundle, "truncated-download", "reject truncated installer without Content-Length", CancellationToken.None, null);
        fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, Response(200, new byte[] { 0x4d, 0x5a, 1, 2, 3, 4, 5 }, null, null, null));
        await RejectDownload(fake, bundle, "oversized-download", "reject installer bytes exceeding declared length", CancellationToken.None, null);
        fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, Data(changed));
        await RejectDownload(fake, bundle, "hash-download", "reject downloaded installer SHA256 mismatch", CancellationToken.None, null);
        Bundle wrongMz = new Bundle("1.11.0", false, new byte[] { 0, 0, 1, 2, 3, 4 });
        fake = new FakeTransport(); fake.Add(wrongMz.InstallerUrl, Data(wrongMz.Payload));
        await RejectDownload(fake, wrongMz, "mz-download", "reject hash-matching installer missing MZ header", CancellationToken.None, null);
        fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, new PluginHttpRead { StatusCode = 200, ContentLength = Installer.Length, Body = new BrokenStream() });
        await RejectDownload(fake, bundle, "interrupted-download", "reject interrupted stream and clean partial file", CancellationToken.None, null);
        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            fake = new FakeTransport(); fake.Add(bundle.InstallerUrl, Data(Installer));
            await RejectDownload(fake, bundle, "cancel-download", "cancel active download and clean partial file", source.Token, new CallbackProgress(delegate(long value) { source.Cancel(); }));
            Check(source.IsCancellationRequested, "active download cancellation occurs after received progress");
        }
        fake = new FakeTransport();
        using (PluginUpdateClient client = new PluginUpdateClient(fake, junction))
        {
            await RejectAsync(delegate { return client.DownloadAsync(bundle.Validated(), null, CancellationToken.None); }, "refuse junction as cache root before HTTP request", delegate(Exception error) { return error is IOException; });
            Check(fake.Calls.Count == 0, "reparse cache refusal sends no request");
        }
    }

    private static async Task RejectCheck(FakeTransport fake, string cache, string name, Predicate<Exception> expected)
    { using (PluginUpdateClient client = Client(fake, cache)) await RejectAsync(delegate { return client.CheckAsync("1.10.0", true, CancellationToken.None); }, name, expected); }
    private static async Task RejectDownload(FakeTransport fake, Bundle bundle, string cache, string name, CancellationToken token, IProgress<long> progress)
    {
        string root = Cache(cache);
        using (PluginUpdateClient client = new PluginUpdateClient(fake, root))
            await RejectAsync(delegate { return client.DownloadAsync(bundle.Validated(), progress, token); }, name, delegate(Exception error) { return token.CanBeCanceled ? error is OperationCanceledException : error is InvalidDataException || error is IOException; });
        Check(NoPartial(root), name + ": no partial file remains");
        Check(!Directory.Exists(root) || Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Length == 0, name + ": no failed installer is staged");
    }
    private static async Task RejectAsync(Func<Task> action, string name, Predicate<Exception> expected)
    { bool rejected = false; try { await action(); } catch (Exception error) { if (!expected(error)) throw new Exception(name + " threw unexpected " + error.GetType().Name, error); rejected = true; } Check(rejected, name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; cases.Add(name); }
    private static bool NoPartial(string root) { return !Directory.Exists(root) || Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Length == 0; }
    private static string Cache(string name) { return Path.Combine(output, "cache-" + name); }
    private static PluginUpdateClient Client(FakeTransport transport, string name) { return new PluginUpdateClient(transport, Cache(name)); }
    private static FakeTransport CheckTransport(Bundle bundle) { FakeTransport result = new FakeTransport(); result.Add(PluginUpdateCore.ApiUrl(1), JsonResponse(Page(bundle.Release), null)); return result; }
    private static string Next(int page) { return "<https://api.github.com/repos/queqideliang/bright-vision/releases?per_page=20&page=" + (page + 1) + ">; rel=\"next\""; }
    private static PluginHttpRead JsonResponse(byte[] bytes, string link) { PluginHttpRead result = Data(bytes); result.Link = link; return result; }
    private static PluginHttpRead Data(byte[] bytes) { return Response(200, bytes, null, null, bytes.Length); }
    private static PluginHttpRead Response(int status, byte[] bytes, string location, string link, long? length) { return new PluginHttpRead { StatusCode = status, Body = new MemoryStream(bytes, false), Location = location, Link = link, ContentLength = length }; }
    private static byte[] Page(params object[] releases) { return Bytes(releases); }
    private static byte[] ViewerPage() { object[] releases = new object[20]; for (int i = 0; i < releases.Length; i++) releases[i] = O("tag_name", "viewer-v99.0." + i); return Page(releases); }
    private static byte[] Bytes(object value) { return Utf8.GetBytes(Json.Encode(value)); }
    private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static Dictionary<string, object> O(params object[] fields) { Dictionary<string, object> result = new Dictionary<string, object>(); for (int i = 0; i < fields.Length; i += 2) result.Add((string)fields[i], fields[i + 1]); return result; }

    private sealed class Bundle
    {
        internal string Version, ManifestUrl, InstallerUrl;
        internal byte[] ManifestBytes, Payload;
        internal Dictionary<string, object> Release;
        internal Bundle(string version, bool preview, byte[] payload)
        {
            Version = version; Payload = payload;
            string tag = "revit-v" + version, name = "BV-" + version + "-Setup.exe", page = PluginUpdateCore.ReleasesPageUrl + "/tag/" + tag;
            ManifestUrl = PluginUpdateCore.ReleasesPageUrl + "/download/" + tag + "/update.json";
            InstallerUrl = PluginUpdateCore.ReleasesPageUrl + "/download/" + tag + "/" + name;
            ManifestBytes = Bytes(O("schemaVersion", 2, "productId", PluginUpdateCore.ProductId, "publisher", PluginUpdateCore.Publisher, "channel", preview ? "preview" : "stable", "version", version,
                "release", O("owner", PluginUpdateCore.Owner, "repository", PluginUpdateCore.Repository, "tag", tag, "url", page),
                "installer", O("fileName", name, "size", payload.Length, "sha256", Hash(payload), "url", InstallerUrl)));
            Release = O("id", Int32.Parse(version.Split('.')[1]) + 1, "tag_name", tag, "draft", false, "prerelease", preview, "html_url", page,
                "assets", new object[] { O("name", "update.json", "state", "uploaded", "size", ManifestBytes.Length, "digest", "sha256:" + Hash(ManifestBytes), "browser_download_url", ManifestUrl), O("name", name, "state", "uploaded", "size", payload.Length, "digest", "sha256:" + Hash(payload), "browser_download_url", InstallerUrl) });
        }
        internal PluginUpdateManifest Validated() { return PluginUpdateCore.ValidateManifest(ManifestBytes, PluginUpdateCore.ParseReleases(Page(Release), true)[0], "1.10.0"); }
    }
    private sealed class CallbackProgress : IProgress<long>
    { private readonly Action<long> callback; internal CallbackProgress(Action<long> callback) { this.callback = callback; } public void Report(long value) { callback(value); } }
    private sealed class FakeTransport : IPluginUpdateTransport
    {
        private readonly Queue<KeyValuePair<string, PluginHttpRead>> pending = new Queue<KeyValuePair<string, PluginHttpRead>>();
        internal readonly List<string> Calls = new List<string>(); internal bool Disposed;
        internal bool Empty { get { return pending.Count == 0; } }
        internal void Add(string address, PluginHttpRead response) { pending.Enqueue(new KeyValuePair<string, PluginHttpRead>(address, response)); }
        public Task<PluginHttpRead> OpenAsync(Uri address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string actual = address.AbsoluteUri; Calls.Add(actual);
            if (pending.Count == 0) throw new Exception("Unexpected fake HTTP request: " + actual);
            KeyValuePair<string, PluginHttpRead> next = pending.Dequeue();
            if (actual != next.Key) throw new Exception("Fake HTTP URL mismatch. Expected " + next.Key + "; received " + actual);
            return Task.FromResult(next.Value);
        }
        public void Dispose() { Disposed = true; while (pending.Count > 0) pending.Dequeue().Value.Dispose(); }
    }
    private sealed class BrokenStream : MemoryStream
    {
        private bool sent;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { if (sent) throw new IOException("Synthetic interrupted HTTP response."); sent = true; buffer[offset] = 0x4d; buffer[offset + 1] = 0x5a; return Task.FromResult(2); }
    }
}
