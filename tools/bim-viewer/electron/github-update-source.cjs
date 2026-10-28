'use strict';

// Main-process only. A fixed GitHub repository is application configuration,
// never renderer input. This module reads public release metadata and stages
// bounded verified bytes; it never runs an installer, shell, script or command.
const https = require('node:https');
const fs = require('node:fs/promises');
const { constants } = require('node:fs');
const path = require('node:path');
const { createHash, randomUUID } = require('node:crypto');
const { compareVersions, PRODUCT_ID, MAX_MANIFEST_BYTES } = require('./update-service.cjs');

const MAX_INSTALLER_BYTES = 2 ** 31 - 1;
const MAX_RELEASE_LIST_BYTES = 2 * 1024 * 1024;
const MAX_RELEASE_PAGES = 3;
const MAX_REDIRECTS = 5;
const PUBLISHER = '崛亮视觉';
const VERSION = /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$/;
const STORAGE_HOSTS = new Set(['release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com']);

class GitHubUpdateError extends Error { constructor(code, message) { super(message); this.code = code; } }
const fail = (code, message) => { throw new GitHubUpdateError(code, message); };
const samePath = (a, b) => process.platform === 'win32' ? path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase() : path.resolve(a) === path.resolve(b);
const identity = (a, b) => a.dev === b.dev && a.ino === b.ino && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
function configured(owner, repository) {
  if (!owner && !repository) return false;
  if (typeof owner !== 'string' || !/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/.test(owner) || owner.includes('--')
    || typeof repository !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(repository) || repository.endsWith('.git')) fail('invalid-source', '官方 GitHub 更新仓库配置无效。');
  return true;
}
function header(response, name) {
  const value = typeof response.headers?.get === 'function' ? response.headers.get(name) : response.headers?.[name.toLowerCase()];
  if (Array.isArray(value)) fail('invalid-response', '更新服务器返回了重复的关键响应头。');
  return value == null ? null : String(value);
}
function cancelled(signal) { if (signal.aborted) fail('cancelled', '更新操作已取消。'); }
async function abortable(promise, signal) {
  cancelled(signal);
  let abort;
  const cancellation = new Promise((_, reject) => { abort = () => reject(new GitHubUpdateError('cancelled', '更新操作已取消。')); signal.addEventListener('abort', abort, { once: true }); });
  try { return await Promise.race([promise, cancellation]); } finally { signal.removeEventListener('abort', abort); }
}
function defaultRequest(url, { signal }) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method: 'GET', headers: { Accept: 'application/vnd.github+json', 'Accept-Encoding': 'identity', 'User-Agent': 'BrightVision-Updater', 'X-GitHub-Api-Version': '2026-03-10' }, signal }, response => {
      resolve({ statusCode: response.statusCode, headers: response.headers, body: response, destroy: () => response.destroy() });
    });
    req.setTimeout(30000, () => req.destroy(new GitHubUpdateError('timeout', '官方更新连接空闲超时。')));
    req.once('error', reject); req.end();
  });
}
function allowedUrl(value, initial, kind, apiBase) {
  let url; try { url = new URL(value); } catch { fail('unsafe-url', '更新地址无效。'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.port || url.hash) fail('unsafe-url', '更新只能使用固定官方 HTTPS 地址。');
  if (kind === 'api') {
    if (url.origin !== 'https://api.github.com' || !url.href.startsWith(apiBase + '/releases?') || url.href !== initial) fail('unsafe-url', '更新 API 不得跳转到其他仓库或服务器。');
  } else if (url.href !== initial) {
    if (!STORAGE_HOSTS.has(url.hostname) || !url.pathname.startsWith('/github-production-release-asset')) fail('unsafe-url', '安装包只能跳转到 GitHub 官方发布文件存储。');
  } else if (url.hostname !== 'github.com' || url.search) fail('unsafe-url', '发布文件地址必须属于固定 GitHub 仓库。');
  return url;
}
async function responseFor(initial, kind, apiBase, request, signal) {
  let current = initial;
  for (let redirects = 0; redirects <= MAX_REDIRECTS; redirects++) {
    cancelled(signal); const url = allowedUrl(current, initial, kind, apiBase);
    const response = await abortable(Promise.resolve().then(() => request(url.href, { signal })), signal);
    const status = response.statusCode ?? response.status;
    if ([301, 302, 303, 307, 308].includes(status)) {
      const location = header(response, 'location'); response.destroy?.();
      if (!location || redirects === MAX_REDIRECTS) fail('redirect-limit', '更新服务器重定向无效或次数过多。');
      try { current = new URL(location, url).href; } catch { fail('unsafe-url', '更新重定向地址无效。'); }
      allowedUrl(current, initial, kind, apiBase); continue;
    }
    if (status !== 200) {
      response.destroy?.();
      if (status === 403 || status === 429) fail('rate-limited', 'GitHub 暂时限制更新请求，请稍后再试。');
      if (status === 404) fail('release-not-found', '官方仓库或发布文件尚未公开。');
      fail('http-error', '无法读取官方发布文件。');
    }
    const encoding = header(response, 'content-encoding');
    if (encoding && encoding.toLowerCase() !== 'identity') { response.destroy?.(); fail('invalid-response', '更新文件不接受压缩的 HTTP 传输编码。'); }
    return response;
  }
  fail('redirect-limit', '更新重定向次数过多。');
}
async function consume(response, maximum, signal, expectedSize, onChunk) {
  const length = header(response, 'content-length');
  if (length !== null && (!/^(0|[1-9]\d*)$/.test(length) || !Number.isSafeInteger(Number(length)) || Number(length) > maximum || expectedSize !== undefined && Number(length) !== expectedSize)) {
    response.destroy?.(); fail('size-mismatch', '更新文件大小无效或与清单不一致。');
  }
  const body = response.body;
  if (!body || typeof body[Symbol.asyncIterator] !== 'function') { response.destroy?.(); fail('invalid-response', '更新服务器未返回可读取的数据。'); }
  const iterator = body[Symbol.asyncIterator](); let bytes = 0, completed = false;
  try {
    for (;;) {
      const part = await abortable(Promise.resolve().then(() => iterator.next()), signal); if (part.done) break;
      if (!(part.value instanceof Uint8Array)) fail('invalid-response', '更新服务器返回无效数据块。');
      const chunk = Buffer.from(part.value.buffer, part.value.byteOffset, part.value.byteLength);
      bytes += chunk.length;
      if (bytes > maximum || expectedSize !== undefined && bytes > expectedSize) fail('size-mismatch', '更新文件超过允许大小。');
      await onChunk(chunk, bytes); cancelled(signal);
    }
    if (length !== null && bytes !== Number(length) || expectedSize !== undefined && bytes !== expectedSize) fail('size-mismatch', '更新文件未完整下载。');
    completed = true; return bytes;
  } finally {
    if (!completed) { response.destroy?.(); try { Promise.resolve(iterator.return?.()).catch(() => {}); } catch { /* Do not wait on a stalled remote iterator. */ } }
  }
}
async function jsonFrom(initial, kind, apiBase, request, signal, maximum, expected) {
  const response = await responseFor(initial, kind, apiBase, request, signal), chunks = [];
  await consume(response, maximum, signal, expected?.size, chunk => { chunks.push(Buffer.from(chunk)); });
  const bytes = Buffer.concat(chunks);
  if (expected?.digest != null && (typeof expected.digest !== 'string' || expected.digest !== 'sha256:' + createHash('sha256').update(bytes).digest('hex'))) fail('hash-mismatch', 'GitHub 清单资源校验值发生变化。');
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\uFEFF/, '')); }
  catch { fail('invalid-manifest', '官方更新信息不是有效的 UTF-8 JSON。'); }
}
function exactKeys(value, allowed) { return value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(key => allowed.includes(key)); }
function validateManifest(value, config, release) {
  if (!exactKeys(value, ['schemaVersion', 'productId', 'publisher', 'channel', 'version', 'release', 'installer', 'components']) || value.schemaVersion !== 2 || value.productId !== PRODUCT_ID || value.publisher !== PUBLISHER || value.channel !== release.channel || value.version !== release.version)
    fail('invalid-manifest', '更新产品、发布者、渠道或版本不匹配。');
  const expectedPage = `https://github.com/${config.owner}/${config.repository}/releases/tag/${release.tag}`;
  if (!exactKeys(value.release, ['owner', 'repository', 'tag', 'url']) || value.release.owner !== config.owner || value.release.repository !== config.repository || value.release.tag !== release.tag || value.release.url !== expectedPage) fail('invalid-manifest', '更新清单引用了其他仓库或发布。');
  const installer = value.installer, fileName = `Bright.Vision-${release.version}-Setup.exe`, url = `https://github.com/${config.owner}/${config.repository}/releases/download/${release.tag}/${fileName}`;
  if (!exactKeys(installer, ['fileName', 'size', 'sha256', 'url']) || installer.fileName !== fileName || installer.url !== url || !Number.isSafeInteger(installer.size) || installer.size < 2 || installer.size > config.maxInstallerBytes || typeof installer.sha256 !== 'string' || !/^[a-f\d]{64}$/.test(installer.sha256)) fail('invalid-manifest', '更新安装包名称、地址、大小或校验值无效。');
  if (value.components !== undefined && (!exactKeys(value.components, ['revitExporterVersion']) || typeof value.components.revitExporterVersion !== 'string' || !VERSION.test(value.components.revitExporterVersion))) fail('invalid-manifest', '内嵌插件版本配置无效。');
  const assets = release.assets.filter(asset => asset.name === fileName);
  if (assets.length !== 1 || assets[0].state !== 'uploaded' || assets[0].size !== installer.size || assets[0].browser_download_url !== url || assets[0].digest != null && assets[0].digest !== `sha256:${installer.sha256}`) fail('invalid-manifest', 'GitHub 安装包资源与更新清单不一致。');
  return { version: value.version, channel: value.channel, installer: { ...installer }, ...(value.components ? { components: { ...value.components } } : {}) };
}
async function plainPath(file, type, create = false) {
  if (typeof file !== 'string' || !path.isAbsolute(file) || file.includes('\0') || /^\\\\[?.]\\/.test(file)) fail('unsafe-path', '更新缓存路径无效。');
  const target = path.resolve(file), root = path.parse(target).root;
  let current = root, info = await fs.lstat(root, { bigint: true });
  for (const part of target.slice(root.length).split(path.sep).filter(Boolean)) {
    if (!info.isDirectory() || info.isSymbolicLink()) fail('unsafe-path', '更新缓存目录不能包含链接。');
    current = path.join(current, part);
    try { info = await fs.lstat(current, { bigint: true }); }
    catch (error) { if (!create || error.code !== 'ENOENT') throw error; try { await fs.mkdir(current, { mode: 0o700 }); } catch (creation) { if (creation.code !== 'EEXIST') throw creation; } info = await fs.lstat(current, { bigint: true }); }
    if (info.isSymbolicLink()) fail('unsafe-path', '更新缓存目录不能包含链接。');
  }
  if (type === 'directory' ? !info.isDirectory() : !info.isFile() || info.nlink !== 1n) fail('unsafe-path', '更新缓存不能包含特殊文件或硬链接。');
  if (!samePath(await fs.realpath(target), target)) fail('unsafe-path', '更新缓存路径发生重定向。');
  return info;
}
async function verifyInstaller(file, installer, signal) {
  const before = await plainPath(file, 'file'); if (before.size !== BigInt(installer.size)) fail('size-mismatch', '缓存安装包大小已改变。');
  const handle = await fs.open(file, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    if (!identity(before, await handle.stat({ bigint: true }))) fail('file-changed', '缓存安装包在检查时已改变。');
    const hash = createHash('sha256'), buffer = Buffer.alloc(1024 * 1024); let bytes = 0;
    for (;;) { cancelled(signal); const part = await handle.read(buffer, 0, buffer.length, null); if (!part.bytesRead) break; bytes += part.bytesRead; if (bytes > installer.size) fail('size-mismatch', '缓存安装包已改变。'); hash.update(buffer.subarray(0, part.bytesRead)); }
    if (bytes !== installer.size || hash.digest('hex') !== installer.sha256) fail('hash-mismatch', '缓存安装包 SHA-256 校验失败。');
    if (!identity(before, await handle.stat({ bigint: true })) || !identity(before, await plainPath(file, 'file'))) fail('file-changed', '缓存安装包在读取时已改变。');
  } finally { await handle.close(); }
}

function createGitHubUpdateSource({ owner, repository, channel = 'preview', currentVersion, userDataPath, request = defaultRequest,
  maxInstallerBytes = MAX_INSTALLER_BYTES, checkTimeoutMs = 30000, downloadTimeoutMs = 600000 } = {}) {
  if (!VERSION.test(currentVersion)) throw new TypeError('Current update version must be x.y.z.');
  const enabled = configured(owner, repository);
  if (!['stable', 'preview'].includes(channel) || !Number.isSafeInteger(maxInstallerBytes) || maxInstallerBytes < 2 || maxInstallerBytes > MAX_INSTALLER_BYTES || typeof request !== 'function'
    || !Number.isSafeInteger(checkTimeoutMs) || checkTimeoutMs < 1 || checkTimeoutMs > 600000 || !Number.isSafeInteger(downloadTimeoutMs) || downloadTimeoutMs < 1 || downloadTimeoutMs > 600000) throw new TypeError('Invalid GitHub update policy.');
  if (typeof userDataPath !== 'string' || !path.isAbsolute(userDataPath)) throw new TypeError('An absolute updater userDataPath is required.');
  const config = { owner, repository, channel, maxInstallerBytes }, apiBase = enabled ? `https://api.github.com/repos/${owner}/${repository}` : '';
  const sourceUrl = enabled ? `https://github.com/${owner}/${repository}/releases` : '';
  let candidate = null, staged = null, active = null;
  let state = { status: 'not-configured', currentVersion, latestVersion: null, code: 'not-configured', message: '尚未配置官方 GitHub 更新源。', checkedAt: null };
  const publicState = () => ({ ...state, ...(state.installer ? { installer: { ...state.installer } } : {}), ...(state.components ? { components: { ...state.components } } : {}), sourceKind: 'github', sourceDirectory: '', sourceUrl, repositoryDisplay: enabled ? `${owner}/${repository}` : '', channel, subscriptionChannel: channel, releaseChannel: candidate?.channel || state.releaseChannel || null, manifestVersion: 2 });
  function result(status, code, message, extra = {}) { state = { status, currentVersion, latestVersion: candidate?.version || null, code, message, checkedAt: state.checkedAt, ...(candidate ? { installer: { ...candidate.installer }, ...(candidate.components ? { components: { ...candidate.components } } : {}) } : {}), ...extra }; return publicState(); }
  async function run(action, options, timeout) {
    if (active) return { ...publicState(), status: 'error', code: 'busy', message: '正在检查或下载更新，请稍候。' };
    const controller = new AbortController(), outer = options?.signal;
    if (outer !== undefined && (typeof outer.addEventListener !== 'function' || typeof outer.aborted !== 'boolean')) throw new TypeError('Invalid cancellation signal.');
    let timedOut = false; const stop = () => controller.abort();
    if (outer?.aborted) controller.abort(); else outer?.addEventListener('abort', stop, { once: true });
    const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeout); active = controller;
    try { cancelled(controller.signal); return await action(controller.signal); }
    catch (error) {
      const code = timedOut ? 'timeout' : controller.signal.aborted ? 'cancelled' : error instanceof GitHubUpdateError ? error.code : 'network-error';
      if (code === 'cancelled') return result('error', code, '更新操作已取消。');
      return result('error', code, timedOut ? '官方更新请求超时，请稍后重试。' : error instanceof GitHubUpdateError ? error.message : '无法读取或保存官方更新文件。');
    } finally { clearTimeout(timer); outer?.removeEventListener('abort', stop); active = null; }
  }
  async function verify(signal) {
    if (!staged) fail('not-staged', '尚未准备好经过校验的安装包。');
    try { await verifyInstaller(staged.path, staged.installer, signal); }
    catch (error) { staged = null; throw error; }
    return result('staged', 'staged', '安装包已校验，等待用户确认安装。', { stagedPath: staged.path });
  }
  return {
    getState: publicState,
    cancel: () => { active?.abort(); },
    check: (options = {}) => run(async signal => {
      candidate = null; staged = null; state.releaseChannel = null; state.checkedAt = new Date().toISOString();
      if (!enabled) return result('not-configured', 'not-configured', '尚未配置官方 GitHub 更新源。');
      const releases = new Map();
      for (let page = 1; page <= MAX_RELEASE_PAGES; page++) {
        const list = await jsonFrom(`${apiBase}/releases?per_page=20&page=${page}`, 'api', apiBase, request, signal, MAX_RELEASE_LIST_BYTES);
        if (!Array.isArray(list) || list.length > 20) fail('invalid-response', 'GitHub 发布列表格式无效。');
        for (const item of list) {
          const match = typeof item?.tag_name === 'string' && /^viewer-v((?:0|[1-9]\d{0,8})\.(?:0|[1-9]\d{0,8})\.(?:0|[1-9]\d{0,8}))$/.exec(item.tag_name);
          // Preview subscribers may graduate to a newer stable release. Stable
          // subscribers never receive prereleases. The selected release's real
          // channel must still agree with its manifest.
          if (!match || item.draft !== false || typeof item.prerelease !== 'boolean' || channel === 'stable' && item.prerelease) continue;
          if (!Number.isSafeInteger(item.id) || item.id < 1 || !Array.isArray(item.assets) || item.assets.length > 1000) fail('invalid-response', 'GitHub 发布身份或资源列表无效。');
          if (item.html_url !== `${sourceUrl}/tag/${item.tag_name}`) fail('invalid-response', 'GitHub 发布页面不属于指定仓库。');
          if (!releases.has(item.id)) releases.set(item.id, { version: match[1], tag: item.tag_name, assets: item.assets, channel: item.prerelease ? 'preview' : 'stable' });
        }
        if (list.length < 20) break;
      }
      const sorted = [...releases.values()].sort((a, b) => compareVersions(b.version, a.version) || Number(b.channel === 'stable') - Number(a.channel === 'stable'));
      if (!sorted.length) return result('error', 'no-compatible-release', '最近发布中尚未找到此产品和渠道的官方版本。');
      const release = sorted[0];
      if (compareVersions(release.version, currentVersion) <= 0) return result('up-to-date', 'same-or-older-version', '当前已是此官方渠道的最新可见版本。', { latestVersion: release.version, releaseChannel: release.channel });
      const manifestUrl = `${sourceUrl}/download/${release.tag}/update.json`, assets = release.assets.filter(asset => asset.name === 'update.json');
      if (assets.length !== 1 || assets[0].state !== 'uploaded' || !Number.isSafeInteger(assets[0].size) || assets[0].size < 1 || assets[0].size > MAX_MANIFEST_BYTES || assets[0].browser_download_url !== manifestUrl) fail('invalid-manifest', '此官方发布缺少有效的更新清单。');
      const manifest = await jsonFrom(manifestUrl, 'asset', apiBase, request, signal, MAX_MANIFEST_BYTES, assets[0]);
      candidate = validateManifest(manifest, config, release);
      return result('update-available', 'update-available', '发现官方新版本，可由用户准备并安装。');
    }, options, checkTimeoutMs),
    stage: (options = {}) => run(async signal => {
      if (!candidate) fail('no-verified-update', '请先检查官方新版本。');
      if (staged) return verify(signal);
      const cache = path.join(userDataPath, 'github-update-cache'); await plainPath(cache, 'directory', true);
      const directory = path.join(cache, `${candidate.version}-${randomUUID()}`); await fs.mkdir(directory, { mode: 0o700 });
      const directoryIdentity = await plainPath(directory, 'directory');
      const temporary = path.join(directory, '.installer.tmp'), destination = path.join(directory, candidate.installer.fileName);
      let handle, renamed = false;
      const ownedDirectory = async () => { const now = await plainPath(directory, 'directory'); if (now.dev !== directoryIdentity.dev || now.ino !== directoryIdentity.ino) fail('unsafe-path', '更新缓存目录已被替换。'); };
      try {
        const response = await responseFor(candidate.installer.url, 'asset', apiBase, request, signal);
        await ownedDirectory(); handle = await fs.open(temporary, 'wx', 0o600);
        const hash = createHash('sha256'); let prefix = Buffer.alloc(0);
        await consume(response, maxInstallerBytes, signal, candidate.installer.size, async chunk => {
          if (prefix.length < 2) { prefix = Buffer.concat([prefix, chunk.subarray(0, 2 - prefix.length)]); if (prefix.length === 2 && (prefix[0] !== 0x4d || prefix[1] !== 0x5a)) fail('invalid-installer', '官方更新文件不是预期的 Windows 安装包。'); }
          hash.update(chunk); let written = 0;
          while (written < chunk.length) { const part = await handle.write(chunk, written, chunk.length - written, null); if (!part.bytesWritten) fail('write-failed', '无法完整保存更新安装包。'); written += part.bytesWritten; }
        });
        if (hash.digest('hex') !== candidate.installer.sha256) fail('hash-mismatch', '官方安装包 SHA-256 校验失败。');
        await handle.sync(); await handle.close(); handle = null; await ownedDirectory();
        await fs.rename(temporary, destination); renamed = true;
        staged = { path: destination, installer: { ...candidate.installer } }; return await verify(signal);
      } catch (error) {
        if (handle) await handle.close().catch(() => {}); staged = null;
        try { await ownedDirectory(); await fs.unlink(renamed ? destination : temporary).catch(() => {}); await fs.rmdir(directory).catch(() => {}); } catch { /* Never traverse/delete a redirected or replaced directory. */ }
        throw error;
      }
    }, options, downloadTimeoutMs),
    verifyStaged: (options = {}) => run(verify, options, downloadTimeoutMs),
  };
}

module.exports = { createGitHubUpdateSource, GitHubUpdateError, MAX_INSTALLER_BYTES, MAX_RELEASE_LIST_BYTES, MAX_RELEASE_PAGES, MAX_REDIRECTS, PUBLISHER };
