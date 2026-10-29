// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrightVision.Update
{
    internal sealed class PluginHttpRead : IDisposable
    {
        internal int StatusCode;
        internal string Location, Link;
        internal long? ContentLength;
        internal Stream Body;
        internal IDisposable Owner;
        public void Dispose() { try { if (Body != null) Body.Dispose(); } finally { if (Owner != null) Owner.Dispose(); } }
    }

    internal interface IPluginUpdateTransport : IDisposable
    {
        Task<PluginHttpRead> OpenAsync(Uri address, CancellationToken token);
    }

    internal sealed class PluginHttpClientTransport : IPluginUpdateTransport
    {
        private readonly HttpClient client;
        internal PluginHttpClientTransport()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            try
            {
                // Old Framework targets can otherwise inherit TLS 1.0 defaults.
                // Configure this handler only; never change Revit-wide TLS or
                // certificate validation. Modern .NET retains OS negotiation.
                if (typeof(object).Assembly.GetName().Name == "mscorlib")
                {
                    PropertyInfo protocol = handler.GetType().GetProperty("SslProtocols");
                    if (protocol != null && protocol.CanWrite) protocol.SetValue(handler, Enum.ToObject(protocol.PropertyType, 3072), null);
                }
                client = new HttpClient(handler, true);
                client.Timeout = TimeSpan.FromSeconds(45);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BrightVision-RevitUpdater/1.11");
            }
            catch { handler.Dispose(); throw; }
        }
        public async Task<PluginHttpRead> OpenAsync(Uri address, CancellationToken token)
        {
            HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                IEnumerable<string> links;
                string link = response.Headers.TryGetValues("Link", out links) ? String.Join(",", links) : null;
                if (link != null && link.Length > 8192) throw new InvalidDataException("更新服务的分页信息过长。");
                Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return new PluginHttpRead { StatusCode = (int)response.StatusCode, Location = response.Headers.Location == null ? null : response.Headers.Location.OriginalString,
                    Link = link, ContentLength = response.Content.Headers.ContentLength, Body = body, Owner = response };
            }
            catch { response.Dispose(); throw; }
        }
        public void Dispose() { client.Dispose(); }
    }

    internal sealed class PluginUpdateCheck
    {
        internal string CurrentVersion;
        internal PluginRelease Latest;
        internal PluginUpdateManifest Available;
        internal bool IncludePreview;
    }

    internal sealed class PluginInstallerDownload
    {
        internal string Path { get; private set; }
        internal PluginUpdateManifest Manifest { get; private set; }
        internal PluginInstallerDownload(string path, PluginUpdateManifest manifest) { Path = path; Manifest = manifest; }
    }

    // No Revit API, model data, authentication, background polling or automatic
    // installer execution. Every request is an anonymous user-initiated GET.
    internal sealed class PluginUpdateClient : IDisposable
    {
        private readonly IPluginUpdateTransport transport;
        private readonly string cacheRoot;
        internal PluginUpdateClient() : this(new PluginHttpClientTransport(), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bright.Vision", "RevitUpdates")) { }
        internal PluginUpdateClient(IPluginUpdateTransport transport, string cacheRoot)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            this.transport = transport; this.cacheRoot = System.IO.Path.GetFullPath(cacheRoot);
        }

        internal async Task<PluginUpdateCheck> CheckAsync(string currentVersion, bool includePreview, CancellationToken token)
        {
            PluginUpdateCore.CompareVersions(currentVersion, currentVersion);
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(60));
                var releases = new List<PluginRelease>();
                for (int page = 1; page <= PluginUpdateCore.MaxReleasePages; page++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    using (PluginHttpRead response = await transport.OpenAsync(new Uri(PluginUpdateCore.ApiUrl(page)), budget.Token).ConfigureAwait(false))
                    {
                        RequireSuccess(response);
                        byte[] bytes = await ReadBoundedAsync(response, PluginUpdateCore.MaxReleaseListBytes, null, budget.Token).ConfigureAwait(false);
                        releases.AddRange(PluginUpdateCore.ParseReleases(bytes, includePreview));
                        bool more = HasNextPage(response.Link, page);
                        if (!more) break;
                        if (page == PluginUpdateCore.MaxReleasePages) throw new InvalidDataException("版本列表超出本次检查范围，检查未完成。请打开官方发布页核对。");
                    }
                }
                PluginRelease latest = PluginUpdateCore.LatestRelease(releases);
                var result = new PluginUpdateCheck { CurrentVersion = currentVersion, Latest = latest, IncludePreview = includePreview };
                if (latest != null && PluginUpdateCore.CompareVersions(latest.Version, currentVersion) > 0)
                {
                    byte[] bytes = await ReadAssetAsync(latest.ManifestAsset, PluginUpdateCore.MaxManifestBytes, budget.Token).ConfigureAwait(false);
                    result.Available = PluginUpdateCore.ValidateManifest(bytes, latest, currentVersion);
                }
                return result;
            }
        }

        internal async Task<PluginInstallerDownload> DownloadAsync(PluginUpdateManifest manifest, IProgress<long> progress, CancellationToken token)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            EnsurePlainPath(cacheRoot);
            Directory.CreateDirectory(cacheRoot); EnsurePlainPath(cacheRoot);
            string directory = System.IO.Path.Combine(cacheRoot, "v" + manifest.Version + "-" + Guid.NewGuid().ToString("N"));
            EnsurePlainPath(directory); Directory.CreateDirectory(directory); EnsurePlainPath(directory);
            string partial = System.IO.Path.Combine(directory, "download.part");
            string destination = System.IO.Path.Combine(directory, manifest.Installer.Name);
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    using (PluginHttpRead response = await OpenAssetAsync(manifest.Installer.Url, budget.Token).ConfigureAwait(false))
                    {
                        RequireLength(response, manifest.Installer.Size);
                        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                        using (budget.Token.Register(delegate { response.Dispose(); }))
                        {
                            byte[] buffer = new byte[65536]; long total = 0;
                            for (;;)
                            {
                                budget.Token.ThrowIfCancellationRequested();
                                int read = await response.Body.ReadAsync(buffer, 0, buffer.Length, budget.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                total += read;
                                if (total > manifest.Installer.Size || total > PluginUpdateCore.MaxInstallerBytes) throw new InvalidDataException("更新文件超过官方声明的大小，已停止下载。");
                                await output.WriteAsync(buffer, 0, read, budget.Token).ConfigureAwait(false);
                                if (progress != null) progress.Report(total);
                            }
                            output.Flush(); output.Position = 0;
                            PluginUpdateCore.VerifyInstaller(output, manifest, budget.Token);
                        }
                    }
                    budget.Token.ThrowIfCancellationRequested(); EnsurePlainPath(directory);
                    File.Move(partial, destination);
                    var result = new PluginInstallerDownload(destination, manifest);
                    VerifyDownloaded(result);
                    return result;
                }
                catch
                {
                    // Only the exact partial file in this new, owned directory.
                    // A completed EXE is never executed or removed on failure.
                    try { EnsurePlainPath(directory); if (File.Exists(partial)) File.Delete(partial); } catch { }
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw;
                }
            }
        }

        private async Task<byte[]> ReadAssetAsync(PluginReleaseAsset asset, int maximum, CancellationToken token)
        {
            using (PluginHttpRead response = await OpenAssetAsync(asset.Url, token).ConfigureAwait(false))
            {
                RequireLength(response, asset.Size);
                byte[] bytes = await ReadBoundedAsync(response, maximum, asset.Size, token).ConfigureAwait(false);
                PluginUpdateCore.VerifyAssetBytes(bytes, asset, maximum); return bytes;
            }
        }
        private async Task<PluginHttpRead> OpenAssetAsync(string initial, CancellationToken token)
        {
            Uri address = PluginUpdateCore.ValidateDownloadUrl(initial, initial, false);
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                token.ThrowIfCancellationRequested();
                PluginHttpRead response = await transport.OpenAsync(address, token).ConfigureAwait(false);
                if (response.StatusCode == 200) return response;
                if (response.StatusCode == 301 || response.StatusCode == 302 || response.StatusCode == 303 || response.StatusCode == 307 || response.StatusCode == 308)
                {
                    string location = response.Location; response.Dispose();
                    if (redirects == 5 || String.IsNullOrWhiteSpace(location)) throw new InvalidDataException("更新文件重定向未完成。");
                    // Validate raw absolute Location before Uri can normalize
                    // escaped traversal or relative path segments.
                    address = PluginUpdateCore.ValidateDownloadUrl(location, initial, true); continue;
                }
                try { RequireSuccess(response); } finally { response.Dispose(); }
            }
            throw new InvalidDataException("更新文件地址未完成校验。");
        }
        private static async Task<byte[]> ReadBoundedAsync(PluginHttpRead response, int maximum, long? exact, CancellationToken token)
        {
            if (response.ContentLength.HasValue && (response.ContentLength.Value < 0 || response.ContentLength.Value > maximum)) throw new InvalidDataException("更新服务响应超过大小限制。");
            using (token.Register(delegate { response.Dispose(); }))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[16384];
                for (;;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = await response.Body.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > maximum || exact.HasValue && output.Length + count > exact.Value) throw new InvalidDataException("更新服务响应超过声明的大小。");
                    output.Write(buffer, 0, count);
                }
                token.ThrowIfCancellationRequested();
                if (exact.HasValue && output.Length != exact.Value) throw new InvalidDataException("更新文件下载不完整。");
                if (response.ContentLength.HasValue && output.Length != response.ContentLength.Value) throw new InvalidDataException("更新服务响应被截断，检查未完成。");
                return output.ToArray();
            }
        }
        private static bool HasNextPage(string header, int page)
        {
            if (String.IsNullOrEmpty(header)) return false;
            if (header.Length > 8192) throw new InvalidDataException("更新服务分页信息过长。");
            bool next = false;
            foreach (string part in header.Split(','))
            {
                Match match = Regex.Match(part.Trim(), "\\A<([^>]+)>;\\s*rel=\"(next|prev|first|last)\"\\z", RegexOptions.CultureInvariant);
                if (!match.Success) throw new InvalidDataException("更新服务分页信息无效，检查未完成。");
                if (match.Groups[2].Value != "next") continue;
                if (next) throw new InvalidDataException("更新服务分页存在重复后续页。"); next = true;
                Uri target;
                if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out target) || target.Scheme != "https" || target.Host != "api.github.com" || target.Port != 443 || target.UserInfo.Length != 0 || target.Fragment.Length != 0 ||
                    target.AbsolutePath != "/repos/queqideliang/bright-vision/releases") throw new InvalidDataException("更新服务分页地址不是官方仓库。");
                string query = target.Query.Substring(1);
                string expectedA = "per_page=20&page=" + (page + 1).ToString(CultureInfo.InvariantCulture), expectedB = "page=" + (page + 1).ToString(CultureInfo.InvariantCulture) + "&per_page=20";
                if (query != expectedA && query != expectedB) throw new InvalidDataException("更新服务分页序号无效。");
            }
            return next;
        }
        private static void RequireSuccess(PluginHttpRead response)
        {
            if (response == null || response.Body == null) throw new InvalidDataException("更新服务未返回完整响应。");
            if (response.StatusCode == 403 || response.StatusCode == 429) throw new InvalidOperationException("官方更新服务暂时限制请求，请稍后再试或打开官方发布页。");
            if (response.StatusCode != 200) throw new InvalidOperationException("更新服务返回 HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + "，检查未完成。");
        }
        private static void RequireLength(PluginHttpRead response, long expected)
        {
            RequireSuccess(response);
            if (response.ContentLength.HasValue && response.ContentLength.Value != expected) throw new InvalidDataException("更新文件长度与官方发布记录不一致。");
        }
        private static void EnsurePlainPath(string path)
        {
            for (string cursor = System.IO.Path.GetFullPath(path); !String.IsNullOrEmpty(cursor); cursor = System.IO.Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新缓存路径不能使用链接或目录联接。");
        }
        internal static void VerifyDownloaded(PluginInstallerDownload download)
        {
            if (download == null || download.Manifest == null || System.IO.Path.GetFileName(download.Path) != download.Manifest.Installer.Name) throw new InvalidDataException("缺少本次已校验的更新安装包。");
            EnsurePlainPath(download.Path);
            using (var stream = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) PluginUpdateCore.VerifyInstaller(stream, download.Manifest, CancellationToken.None);
        }
        internal static void LaunchInstaller(PluginInstallerDownload download)
        {
            VerifyDownloaded(download);
            using (var stream = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PluginUpdateCore.VerifyInstaller(stream, download.Manifest, CancellationToken.None);
                Process.Start(new ProcessStartInfo(download.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(download.Path) });
            }
        }
        internal static void OpenOfficialPage(string version)
        {
            string url = PluginUpdateCore.ReleasesPageUrl;
            if (version != null) { PluginUpdateCore.CompareVersions(version, version); url += "/tag/revit-v" + version; }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        public void Dispose() { transport.Dispose(); }
    }
}
