import assert from 'node:assert/strict';
import test from 'node:test';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { lstat, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { createGitHubUpdateSource, MAX_RELEASE_LIST_BYTES, MAX_INSTALLER_BYTES } = require('../electron/github-update-source.cjs');
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const testRoot = path.join(project, 'test-results');
const sha = (bytes: Uint8Array) => createHash('sha256').update(bytes).digest('hex');
const owner = 'BrightVision', repository = 'bright-vision';
const base = `https://github.com/${owner}/${repository}/releases`;
const api = `https://api.github.com/repos/${owner}/${repository}/releases?per_page=20&page=`;
const binary = Buffer.from('MZ inert test fixture, never executable or launched\n');

async function sandbox(t: any) {
  await mkdir(testRoot, { recursive: true });
  const root = await mkdtemp(path.join(testRoot, 'github-update-source-'));
  t.after(async () => { assert.ok(path.resolve(root).startsWith(path.resolve(testRoot) + path.sep)); assert.ok(!(await lstat(root)).isSymbolicLink()); await rm(root, { recursive: true, force: true }); });
  return root;
}
function response(bytes: Uint8Array | string, statusCode = 200, headers: Record<string, string> = {}) {
  const value = typeof bytes === 'string' ? Buffer.from(bytes) : Buffer.from(bytes);
  return { statusCode, headers, async *body() { yield value; }, destroy() {}, };
}
function reply(bytes: Uint8Array | string, statusCode = 200, headers: Record<string, string> = {}) {
  const value = response(bytes, statusCode, headers); return { ...value, body: value.body() };
}
function publication(version = '0.6.0', channel = 'preview', bytes = binary) {
  const tag = `viewer-v${version}`, fileName = `Bright.Vision-${version}-Setup.exe`;
  const manifest = { schemaVersion: 2, productId: 'local.bim.modelviewer', publisher: '崛亮视觉', channel, version,
    release: { owner, repository, tag, url: `${base}/tag/${tag}` },
    installer: { fileName, size: bytes.length, sha256: sha(bytes), url: `${base}/download/${tag}/${fileName}` }, components: { revitExporterVersion: '1.7.0' } };
  const manifestBytes = Buffer.from(JSON.stringify(manifest));
  const release = { id: Number(version.split('.').join('')) + 1, tag_name: tag, draft: false, prerelease: channel === 'preview', html_url: manifest.release.url,
    assets: [{ name: 'update.json', state: 'uploaded', size: manifestBytes.length, digest: `sha256:${sha(manifestBytes)}`, browser_download_url: `${base}/download/${tag}/update.json` },
      { name: fileName, state: 'uploaded', size: bytes.length, digest: `sha256:${sha(bytes)}`, browser_download_url: manifest.installer.url }] };
  return { manifest, manifestBytes, release, bytes };
}
function transport(pub: ReturnType<typeof publication>, overrides: Map<string, () => any> = new Map()) {
  const calls: string[] = [];
  const request = async (url: string, options: any) => {
    calls.push(url); assert.ok(options.signal);
    if (overrides.has(url)) return overrides.get(url)!();
    if (url === api + '1') return reply(JSON.stringify([pub.release]));
    if (url === `${base}/download/${pub.release.tag_name}/update.json`) return reply(pub.manifestBytes);
    if (url === pub.manifest.installer.url) return reply(pub.bytes);
    throw new Error('Unexpected mocked request URL.');
  };
  return { calls, request };
}
function service(root: string, request: any, extra: any = {}) { return createGitHubUpdateSource({ owner, repository, channel: 'preview', currentVersion: '0.5.2', userDataPath: root, request, ...extra }); }
async function cacheFiles(root: string) {
  const cache = path.join(root, 'github-update-cache');
  try { return await readdir(cache); } catch (error: any) { if (error.code === 'ENOENT') return []; throw error; }
}

test('unconfigured official repository performs no network request or staging', async t => {
  const root = await sandbox(t); let requests = 0;
  const updater = createGitHubUpdateSource({ currentVersion: '0.6.0', userDataPath: root, request: async () => { requests++; throw new Error(); } });
  assert.equal((await updater.check()).status, 'not-configured'); assert.equal(requests, 0);
  assert.equal((await updater.stage()).code, 'no-verified-update'); assert.deepEqual(await cacheFiles(root), []);
});
test('GitHub update stages exact bounded bytes and detects changed staged installer before execution', async t => {
  const root = await sandbox(t), pub = publication(), mock = transport(pub), updater = service(root, mock.request);
  const checked = await updater.check(); assert.equal(checked.status, 'update-available'); assert.equal(checked.sourceKind, 'github'); assert.equal(checked.manifestVersion, 2);
  assert.equal(checked.repositoryDisplay, `${owner}/${repository}`); assert.equal(checked.components.revitExporterVersion, '1.7.0');
  assert.equal(mock.calls.length, 2, 'check must not download the EXE');
  const staged = await updater.stage(); assert.equal(staged.status, 'staged'); assert.deepEqual(await readFile(staged.stagedPath), binary);
  assert.equal((await updater.verifyStaged()).status, 'staged');
  await writeFile(staged.stagedPath, Buffer.from('MZ changed inert fixture of any size'));
  assert.match((await updater.verifyStaged()).code, /size-mismatch|hash-mismatch/); assert.equal((await updater.verifyStaged()).code, 'not-staged');
});
test('numeric version selection skips drafts and other products across eligible channels', async t => {
  const root = await sandbox(t), pub = publication('0.7.0'), mock = transport(pub);
  mock.request = transport(pub, new Map([[api + '1', () => reply(JSON.stringify([
    { ...publication('9.0.0').release, draft: true }, publication('0.6.0', 'stable').release,
    { ...publication('9.1.0').release, tag_name: 'revit-v9.1.0' }, pub.release, publication('0.6.0').release,
  ]))]])).request;
  assert.equal((await service(root, mock.request).check()).latestVersion, '0.7.0');
  assert.equal((await service(root, mock.request, { currentVersion: '0.7.0' }).check()).status, 'up-to-date');
});

test('preview subscription receives a newer stable release and validates its actual release channel', async t => {
  const root = await sandbox(t), stable = publication('1.0.0', 'stable');
  const mock = transport(stable, new Map([[api + '1', () => reply(JSON.stringify([publication('0.9.0').release, stable.release]))]]));
  const updater = service(root, mock.request), checked = await updater.check();
  assert.equal(checked.status, 'update-available'); assert.equal(checked.latestVersion, '1.0.0');
  assert.equal(checked.subscriptionChannel, 'preview'); assert.equal(checked.releaseChannel, 'stable');
  assert.equal((await updater.stage()).status, 'staged');
  const same = await service(root, mock.request, { currentVersion: '1.0.0' }).check();
  assert.equal(same.status, 'up-to-date'); assert.equal(same.releaseChannel, 'stable');
});

test('stable subscription never receives a higher preview and rejects a manifest disguising its release channel', async t => {
  const root = await sandbox(t), stable = publication('0.6.0', 'stable');
  const mock = transport(stable, new Map([[api + '1', () => reply(JSON.stringify([publication('9.0.0').release, stable.release]))]]));
  const checked = await service(root, mock.request, { channel: 'stable' }).check();
  assert.equal(checked.status, 'update-available'); assert.equal(checked.latestVersion, '0.6.0');
  assert.equal(checked.subscriptionChannel, 'stable'); assert.equal(checked.releaseChannel, 'stable');
  stable.manifest.channel = 'preview'; stable.manifestBytes = Buffer.from(JSON.stringify(stable.manifest));
  stable.release.assets[0].size = stable.manifestBytes.length; stable.release.assets[0].digest = `sha256:${sha(stable.manifestBytes)}`;
  assert.equal((await service(root, transport(stable).request).check()).code, 'invalid-manifest');
});
test('pagination is bounded at three pages, duplicate release IDs do not multiply candidates', async t => {
  const root = await sandbox(t), pub = publication(), calls: string[] = [];
  const request = async (url: string) => { calls.push(url); if (url.startsWith(api)) return reply(JSON.stringify(Array.from({ length: 20 }, () => pub.release))); return reply(pub.manifestBytes); };
  assert.equal((await service(root, request).check()).status, 'update-available');
  assert.equal(calls.filter(url => url.startsWith(api)).length, 3); assert.ok(!calls.some(url => url.endsWith('page=4')));
});
test('redirects accept a GitHub official release storage path and split MZ header across chunks', async t => {
  const root = await sandbox(t), pub = publication();
  const storage = 'https://release-assets.githubusercontent.com/github-production-release-asset/123/abc?public-signed-download=value';
  const mock = transport(pub, new Map([[pub.manifest.installer.url, () => reply('', 302, { location: storage })], [storage, () => ({ statusCode: 200, headers: {}, body: (async function* () { yield binary.subarray(0, 1); yield binary.subarray(1); })() })]]));
  const updater = service(root, mock.request); await updater.check();
  assert.equal((await updater.stage()).status, 'staged'); assert.ok(mock.calls.includes(storage));
});
test('redirect guard rejects downgrade, credentials, other repo, nondefault port, unrelated host and storage path', async t => {
  const root = await sandbox(t), pub = publication();
  for (const destination of ['http://github.com/x/y', 'https://user:pw@release-assets.githubusercontent.com/github-production-release-asset/a', 'https://github.com/elsewhere/repo/releases/download/tag/file.exe',
    'https://release-assets.githubusercontent.com:8443/github-production-release-asset/a', 'https://evil.invalid/payload.exe', 'https://objects.githubusercontent.com/arbitrary-file']) {
    const mock = transport(pub, new Map([[pub.manifest.installer.url, () => reply('', 302, { location: destination })]])), updater = service(root, mock.request);
    await updater.check(); assert.equal((await updater.stage()).code, 'unsafe-url'); assert.ok(!mock.calls.includes(destination));
  }
  assert.deepEqual(await cacheFiles(root), []);
});
test('API redirects cannot silently move the product to another owner or host', async t => {
  const root = await sandbox(t), pub = publication(), next = 'https://api.github.com/repos/OtherOwner/bright-vision/releases?per_page=20&page=1';
  const mock = transport(pub, new Map([[api + '1', () => reply('', 301, { location: next })]]));
  assert.equal((await service(root, mock.request).check()).code, 'unsafe-url'); assert.deepEqual(mock.calls, [api + '1']);
});
test('manifest mismatch or executable command fields are rejected before installer requests', async t => {
  const root = await sandbox(t);
  for (const mutate of [(m: any) => m.publisher = 'Someone else', (m: any) => m.channel = 'stable', (m: any) => m.version = '0.6.1',
    (m: any) => m.productId = 'other.product', (m: any) => m.release.owner = 'OtherOwner', (m: any) => m.installer.url = 'https://evil.invalid/payload.exe',
    (m: any) => m.installer.fileName = '../payload.exe', (m: any) => m.installer.args = ['/silent'], (m: any) => m.command = 'powershell.exe']) {
    const pub = publication(); mutate(pub.manifest); pub.manifestBytes = Buffer.from(JSON.stringify(pub.manifest));
    pub.release.assets[0].size = pub.manifestBytes.length; pub.release.assets[0].digest = `sha256:${sha(pub.manifestBytes)}`;
    const mock = transport(pub); assert.equal((await service(root, mock.request).check()).code, 'invalid-manifest'); assert.equal(mock.calls.length, 2);
  }
});
test('bounded HTTP and JSON bodies reject overflow, invalid UTF-8 and compression', async t => {
  const root = await sandbox(t), pub = publication();
  for (const make of [() => reply(Buffer.alloc(MAX_RELEASE_LIST_BYTES + 1)), () => reply('[', 200, { 'content-length': String(MAX_RELEASE_LIST_BYTES + 1) }),
    () => reply(Buffer.from([0xff])), () => reply('[]', 200, { 'content-encoding': 'gzip' })]) {
    const mock = transport(pub, new Map([[api + '1', make]])); assert.equal((await service(root, mock.request).check()).status, 'error');
  }
  const big = Buffer.alloc(65537), mock = transport(pub, new Map([[pub.release.assets[0].browser_download_url, () => reply(big)]]));
  assert.equal((await service(root, mock.request).check()).code, 'size-mismatch');
});
test('GitHub digest and exact manifest resource length bind metadata to bytes', async t => {
  const root = await sandbox(t);
  for (const modify of [(pub: any) => pub.release.assets[0].digest = 'sha256:' + '0'.repeat(64), (pub: any) => pub.release.assets[0].size++, (pub: any) => pub.release.assets[1].digest = 'sha256:' + '0'.repeat(64)]) {
    const pub = publication(); modify(pub); const updater = service(root, transport(pub).request); assert.equal((await updater.check()).status, 'error');
  }
});
test('installer hash, header, declared length and streamed size all fail closed and remove only owned partial files', async t => {
  const root = await sandbox(t), pub = publication();
  for (const make of [() => reply(Buffer.from(binary.toString().replace('inert', 'INERT'))), () => reply(binary.subarray(0, -1)), () => reply(Buffer.concat([binary, Buffer.from('extra')])),
    () => reply(binary, 200, { 'content-length': String(binary.length + 1) }), () => reply(Buffer.from('not a PE file'))]) {
    const updater = service(root, transport(pub, new Map([[pub.manifest.installer.url, make]])).request); await updater.check(); assert.equal((await updater.stage()).status, 'error');
    assert.deepEqual(await cacheFiles(root), []);
  }
});
test('cancellation during a stalled transfer removes partial cache and makes no installer available', async t => {
  const root = await sandbox(t), pub = publication(); let firstChunk: () => void;
  const started = new Promise<void>(resolve => { firstChunk = resolve; });
  const updater = service(root, transport(pub, new Map([[pub.manifest.installer.url, () => ({ statusCode: 200, headers: {}, body: (async function* () { yield binary.subarray(0, 4); firstChunk!(); await new Promise(() => {}); })() })]])).request);
  await updater.check(); const stage = updater.stage(); await started; updater.cancel();
  assert.equal((await stage).code, 'cancelled'); assert.deepEqual(await cacheFiles(root), []); assert.equal((await updater.verifyStaged()).code, 'not-staged');
});
test('header wait and body wait have overall timeouts, and a second operation is busy', async t => {
  const root = await sandbox(t), updater = service(root, async () => new Promise(() => {}), { checkTimeoutMs: 25 });
  const pending = updater.check(); assert.equal((await updater.check()).code, 'busy'); assert.equal((await pending).code, 'timeout');
  const pub = publication(), stalled = service(root, transport(pub, new Map([[pub.manifest.installer.url, () => ({ statusCode: 200, headers: {}, body: (async function* () { await new Promise(() => {}); yield binary; })() })]])).request, { downloadTimeoutMs: 25 });
  await stalled.check(); assert.equal((await stalled.stage()).code, 'timeout'); assert.deepEqual(await cacheFiles(root), []);
});
test('redirect loops stop after five hops without requesting arbitrary resources', async t => {
  const root = await sandbox(t), pub = publication(); let redirects = 0;
  const mock = transport(pub, new Map([[pub.manifest.installer.url, () => { redirects++; return reply('', 302, { location: pub.manifest.installer.url }); }]]));
  const updater = service(root, mock.request); await updater.check(); assert.equal((await updater.stage()).code, 'redirect-limit'); assert.equal(redirects, 6);
});
test('no compatible release and rate limits do not claim an online update is ready', async t => {
  const root = await sandbox(t);
  assert.equal((await service(root, async () => reply('[]')).check()).code, 'no-compatible-release');
  assert.equal((await service(root, async () => reply('', 429)).check()).code, 'rate-limited');
  assert.equal((await service(root, async () => reply('', 404)).check()).code, 'release-not-found');
});
test('unsafe config, limits and cache symlinks cannot cause arbitrary requests or outside writes', async t => {
  const root = await sandbox(t), pub = publication(); let network = 0;
  assert.throws(() => service(root, async () => { network++; }, { owner: 'https://evil.invalid' }));
  assert.throws(() => service(root, async () => {}, { repository: '../other' }));
  assert.throws(() => service(root, async () => {}, { maxInstallerBytes: MAX_INSTALLER_BYTES + 1 })); assert.equal(network, 0);
  const elsewhere = path.join(root, 'elsewhere'); await mkdir(elsewhere);
  try { await symlink(elsewhere, path.join(root, 'github-update-cache'), process.platform === 'win32' ? 'junction' : 'dir'); }
  catch (error: any) { if (error.code === 'EPERM' || error.code === 'EACCES') { t.skip('OS does not permit test-only links in this session.'); return; } throw error; }
  const updater = service(root, transport(pub).request); await updater.check(); assert.equal((await updater.stage()).code, 'unsafe-path'); assert.deepEqual(await readdir(elsewhere), []);
});
