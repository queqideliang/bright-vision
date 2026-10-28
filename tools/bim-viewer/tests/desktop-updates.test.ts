import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdtemp, mkdir, readFile, writeFile, readdir, realpath, rm } from 'node:fs/promises';

const modulePath = fileURLToPath(new URL('../electron/desktop-updates.cjs', import.meta.url));
const realRequire = createRequire(modulePath);

async function harness(t: any, options: any = {}) {
  const temporary = await realpath(tmpdir());
  const root = await mkdtemp(path.join(temporary, 'bright-desktop-update-'));
  t.after(async () => { assert.equal(path.dirname(root), temporary); await rm(root, { recursive: true, force: true }); });
  const userData = path.join(root, 'preferences'), executable = path.join(root, 'install location', 'BIM Model Viewer.exe');
  const feed = path.join(root, 'feed'); await mkdir(userData); await mkdir(feed); await mkdir(path.dirname(executable));
  const bytes = Buffer.from('Synthetic package. The mocked process launcher never executes it.');
  const fileName = 'BIM-Model-Viewer-0.4.0-Setup.exe';
  await writeFile(path.join(feed, fileName), bytes);
  await writeFile(path.join(feed, 'update.json'), JSON.stringify({ schemaVersion: 1, productId: 'local.bim.modelviewer', channel: 'stable', version: '0.4.0',
    installer: { fileName, size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') } }));
  if (options.settings) await writeFile(path.join(userData, 'desktop-settings.json'), JSON.stringify(options.settings === true ? { language: 'en', feedDirectory: feed } : options.settings));
  const app: any = new EventEmitter();
  app.getVersion = () => '0.3.0'; app.getPath = (name: string) => name === 'userData' ? userData : executable;
  let quit = 0; app.quit = () => { quit++; };
  const handlers = new Map<string, (...args: any[]) => any>();
  const notifications: any[] = [], languages: string[] = [], launches: any[] = [];
  const event = {}, window = { isDestroyed: () => false, webContents: { send: (channel: string, state: any) => notifications.push({ channel, state }) } };
  const dialogs: any = {
    showOpenDialog: async () => ({ canceled: false, filePaths: [feed] }),
    showMessageBox: async () => ({ response: 1 }),
  };
  const electron = { app, ipcMain: { handle: (channel: string, handler: any) => handlers.set(channel, handler) }, dialog: dialogs };
  const spawn = (...args: any[]) => {
    launches.push(args);
    const child: any = new EventEmitter(); child.unref = () => {};
    queueMicrotask(() => child.emit(options.spawnError ? 'error' : 'spawn', new Error('mock launch failure')));
    return child;
  };
  // An isolated CommonJS wrapper supplies Electron/process doubles locally;
  // require.cache and Node's global module loader remain untouched.
  const context = vm.createContext({ Buffer, console, setInterval: () => ({ unref() {} }), clearInterval() {} });
  const wrapper = new vm.Script(`(function(require,module,exports){\n${await readFile(modulePath, 'utf8')}\n})`, { filename: modulePath }).runInContext(context);
  const loaded = { exports: {} as any };
  wrapper((name: string) => name === 'electron' ? electron : name === 'node:child_process' ? { spawn } : realRequire(name), loaded, loaded.exports);
  const updates = await loaded.exports.setupDesktopUpdates({ getWindow: () => window, trusted: (value: any) => value === event, onLanguage: (value: string) => languages.push(value),
    officialReleaseConfig: options.officialReleaseConfig || { schemaVersion: 1, enabled: false, owner: '', repository: '', channel: 'preview', productId: 'local.bim.modelviewer' },
    ...(options.githubRequest ? { githubRequest: options.githubRequest } : {}) });
  const invoke = (channel: string, ...args: any[]) => handlers.get(channel)!(event, ...args);
  return { root, feed, userData, bytes, updates, invoke, handlers, event, dialogs, notifications, languages, launches, get quit() { return quit; } };
}

test('desktop update handlers reject an untrusted event before performing work', async t => {
  const h = await harness(t);
  for (const handler of h.handlers.values()) await assert.rejects(async () => handler({}, 'en'), /Untrusted application frame/);
  assert.equal(h.launches.length, 0);
});

test('persisted source and language initialize before automatic check', async t => {
  const h = await harness(t, { settings: true });
  assert.deepEqual(h.languages, ['en']);
  const info = await h.invoke('bim:desktop-info');
  assert.equal(info.language, 'en');
  assert.equal(info.update.source, h.feed);
  const state = await h.updates.check();
  assert.equal(state.status, 'update-available');
  assert.equal(state.latestVersion, '0.4.0');
  assert.equal(state.busy, false);
  assert.equal(state.stagedPath, undefined);
});

test('native source selection persists the chosen directory without accepting a renderer path', async t => {
  const h = await harness(t);
  const state = await h.invoke('bim:update-source', 'C:\\renderer-injected-path');
  assert.equal(state.source, h.feed);
  assert.equal(state.status, 'update-available');
  const preferences = JSON.parse(await readFile(path.join(h.userData, 'desktop-settings.json'), 'utf8'));
  assert.equal(preferences.feedDirectory, h.feed);
  await h.invoke('bim:desktop-language', 'en');
  assert.equal(JSON.parse(await readFile(path.join(h.userData, 'desktop-settings.json'), 'utf8')).language, 'en');
});

test('canceling installation does not launch or quit and returns an installable state', async t => {
  const h = await harness(t, { settings: true }); await h.updates.check();
  const state = await h.invoke('bim:update-install');
  assert.equal(state.status, 'update-available');
  assert.equal(state.stagedPath, undefined);
  assert.equal(h.launches.length, 0);
  assert.equal(h.quit, 0);
  assert.equal(state.busy, false);
});

test('a cache modified while confirmation is open fails final verification and never launches', async t => {
  const h = await harness(t, { settings: true }); await h.updates.check();
  h.dialogs.showMessageBox = async () => {
    const cache = path.join(h.userData, 'update-cache');
    const [directory] = await readdir(cache);
    const [file] = await readdir(path.join(cache, directory!));
    await writeFile(path.join(cache, directory!, file!), Buffer.alloc(h.bytes.length, 0x41));
    return { response: 0 };
  };
  const state = await h.invoke('bim:update-install');
  assert.equal(state.status, 'error');
  assert.equal(state.code, 'hash-mismatch');
  assert.equal(h.launches.length, 0);
  assert.equal(h.quit, 0);
  assert.equal(state.stagedPath, undefined);
});

test('confirmed install launches only a verified private copy and quits after spawn succeeds', async t => {
  const h = await harness(t, { settings: true }); await h.updates.check();
  h.dialogs.showMessageBox = async () => ({ response: 0 });
  const state = await h.invoke('bim:update-install');
  assert.equal(h.launches.length, 1);
  const [file, args, options] = h.launches[0];
  assert(file.startsWith(path.join(h.userData, 'update-cache') + path.sep));
  assert.notEqual(path.dirname(file), h.feed);
  assert.deepEqual(await readFile(file), h.bytes);
  assert.equal(args.length, 0, 'NSIS remembers its installed directory; do not send quoted /D arguments.');
  assert.equal(options.shell, undefined);
  assert.equal(h.quit, 1);
  assert.equal(state.stagedPath, undefined);
  assert(h.notifications.every(row => row.state.stagedPath === undefined));
});

test('installer spawn errors keep the viewer running with a structured failure state', async t => {
  const h = await harness(t, { settings: true, spawnError: true }); await h.updates.check();
  h.dialogs.showMessageBox = async () => ({ response: 0 });
  const state = await h.invoke('bim:update-install');
  assert.equal(state.code, 'INSTALL_FAILED');
  assert.equal(state.busy, false);
  assert.equal(h.quit, 0);
});

test('unconfigured official source ignores renderer URL arguments and persists only source selection', async t => {
  let requested = 0; const h = await harness(t, { settings: true, githubRequest: async () => { requested++; throw new Error('No remote configuration should request data.'); } });
  await h.updates.check();
  const state = await h.invoke('bim:update-official', 'https://renderer.invalid/feed.json');
  assert.equal(state.sourceKind, 'github'); assert.equal(state.officialConfigured, false); assert.equal(state.status, 'not-configured'); assert.equal(state.source, ''); assert.equal(requested, 0);
  const settings = JSON.parse(await readFile(path.join(h.userData, 'desktop-settings.json'), 'utf8'));
  assert.equal(settings.sourceKind, 'github'); assert.equal(settings.owner, undefined); assert.equal(settings.url, undefined);
  assert.equal((await h.invoke('bim:update-source')).sourceKind, 'local');
});

test('invalid build source config fails closed instead of crashing the local viewer or requesting arbitrary servers', async t => {
  let requested = 0; const h = await harness(t, { officialReleaseConfig: { schemaVersion: 1, enabled: true, productId: 'local.bim.modelviewer', owner: 'https://evil.invalid', repository: 'repo', channel: 'preview' },
    githubRequest: async () => { requested++; throw new Error(); } });
  const state = await h.updates.check(); assert.equal(state.status, 'not-configured'); assert.equal(state.officialConfigured, false); assert.equal(requested, 0);
});

test('configured GitHub updates need confirmation and launch only their reverified private installer copy', async t => {
  const bytes = Buffer.from('MZ inert mocked GitHub installer; never launched by these tests.');
  const owner = 'BrightVision', repository = 'bright-vision', tag = 'viewer-v0.6.0', fileName = 'Bright.Vision-0.6.0-Setup.exe';
  const base = `https://github.com/${owner}/${repository}/releases`;
  const manifest = { schemaVersion: 2, productId: 'local.bim.modelviewer', publisher: '崛亮视觉', channel: 'preview', version: '0.6.0', release: { owner, repository, tag, url: `${base}/tag/${tag}` },
    installer: { fileName, size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'), url: `${base}/download/${tag}/${fileName}` } };
  const manifestBytes = Buffer.from(JSON.stringify(manifest));
  const release = { id: 1, tag_name: tag, html_url: manifest.release.url, draft: false, prerelease: true, assets: [
    { name: 'update.json', size: manifestBytes.length, state: 'uploaded', browser_download_url: `${base}/download/${tag}/update.json` },
    { name: fileName, size: bytes.length, state: 'uploaded', browser_download_url: manifest.installer.url },
  ] };
  const requested: string[] = [];
  const githubRequest = async (url: string) => { requested.push(url); const data = url.includes('/repos/') ? Buffer.from(JSON.stringify([release])) : url.endsWith('/update.json') ? manifestBytes : bytes;
    return { statusCode: 200, headers: {}, body: (async function* () { yield data; })() }; };
  const h = await harness(t, { officialReleaseConfig: { schemaVersion: 1, enabled: true, owner, repository, productId: 'local.bim.modelviewer', channel: 'preview' }, githubRequest });
  const checked = await h.updates.check(); assert.equal(checked.status, 'update-available'); assert.equal(checked.sourceKind, 'github'); assert.equal(checked.officialConfigured, true); assert.equal(checked.source, base);
  await h.invoke('bim:update-install'); assert.equal(h.launches.length, 0); assert.equal(h.quit, 0, 'default dialog cancels');
  assert.equal(requested.filter(url => url.endsWith('.exe')).length, 1);
  h.dialogs.showMessageBox = async () => ({ response: 0 });
  await h.invoke('bim:update-install'); assert.equal(h.launches.length, 1); assert.equal(h.quit, 1);
  const [file, args, options] = h.launches[0]; assert.ok(file.startsWith(path.join(h.userData, 'github-update-cache') + path.sep)); assert.deepEqual(await readFile(file), bytes);
  assert.equal(args.length, 0); assert.equal(options.shell, undefined); assert.ok(h.notifications.every(row => row.state.stagedPath === undefined));
});
