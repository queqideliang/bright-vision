import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { mkdtemp, mkdir, writeFile, readFile, readdir, rm, realpath, symlink, link, unlink, rename, lstat } from 'node:fs/promises';

const require = createRequire(import.meta.url);
const { createUpdateService, compareVersions, installerFileNames } = require('../electron/update-service.cjs');
const hash = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');

async function fixture(t: any, options: any = {}) {
  const base = await realpath(tmpdir());
  const root = await mkdtemp(path.join(base, 'bright-viewer-update-'));
  // mkdtemp owns this unique test folder, including all symlink test entries.
  t.after(async () => { assert(path.dirname(root) === base); await rm(root, { recursive: true, force: true }); });
  const feed = path.join(root, 'feed'), userData = path.join(root, 'user-data');
  await mkdir(feed); await mkdir(userData);
  const bytes = Buffer.from('Synthetic installer fixture. It must never be executed.');
  const manifest = {
    schemaVersion: 1, productId: 'local.bim.modelviewer', channel: 'stable', version: '0.4.0',
    installer: { fileName: 'BIM-Model-Viewer-0.4.0-Setup.exe', sha256: hash(bytes), size: bytes.length },
  };
  const file = path.join(feed, manifest.installer.fileName);
  await writeFile(file, bytes);
  const save = () => writeFile(path.join(feed, 'update.json'), JSON.stringify(manifest));
  await save();
  const service = createUpdateService({ currentVersion: '0.3.0', userDataPath: userData, ...options });
  return { root, feed, userData, bytes, manifest, file, save, service };
}

test('numeric semantic versions reject ambiguous formats and compare each segment', () => {
  assert.equal(compareVersions('1.10.0', '1.9.99'), 1);
  assert.equal(compareVersions('2.0.0', '10.0.0'), -1);
  assert.equal(compareVersions('1.2.3', '1.2.3'), 0);
  for (const bad of ['01.2.3', '1.2', '1.2.3-beta', '1.2.3+meta', '-1.2.3', '1000000000.0.0'])
    assert.throws(() => compareVersions(bad, '1.0.0'));
});

test('unified and legacy installer basenames remain exact version-bound allowlisted names', async t => {
  assert.deepEqual(installerFileNames('0.5.0'), ['Bright.Vision-0.5.0-Setup.exe', 'BIM-Model-Viewer-0.5.0-Setup.exe']);
  const f = await fixture(t);
  f.manifest.installer.fileName = 'Bright.Vision-0.4.0-Setup.exe';
  await rename(f.file, path.join(f.feed, f.manifest.installer.fileName)); await f.save();
  assert.equal((await f.service.check(f.feed)).status, 'update-available');
  const staged = await f.service.stage();
  assert.equal(staged.status, 'staged');
  assert.equal(path.basename(staged.stagedPath), 'Bright.Vision-0.4.0-Setup.exe');
  assert.deepEqual(await readFile(staged.stagedPath), f.bytes);
});

test('check is read-only; stage makes an independent verified cache copy', async t => {
  const f = await fixture(t);
  assert.equal(f.service.getState().status, 'not-configured');
  const checked = await f.service.check(f.feed);
  assert.equal(checked.status, 'update-available');
  assert.equal(checked.currentVersion, '0.3.0');
  assert.equal(checked.latestVersion, '0.4.0');
  assert.equal(checked.stagedPath, undefined);
  assert.deepEqual(await readdir(f.userData), []);
  assert.deepEqual((await readdir(f.feed)).sort(), [f.manifest.installer.fileName, 'update.json'].sort());
  const staged = await f.service.stage();
  assert.equal(staged.status, 'staged');
  assert(staged.stagedPath.startsWith(path.join(f.userData, 'update-cache') + path.sep));
  assert.notEqual(staged.stagedPath, f.file);
  assert.deepEqual(await readFile(staged.stagedPath), f.bytes);
  assert.deepEqual(await readdir(path.dirname(staged.stagedPath)), [f.manifest.installer.fileName]);
  await unlink(f.file);
  assert.equal((await f.service.verifyStaged()).status, 'staged');
});

test('same and older versions never become installable candidates', async t => {
  for (const version of ['0.3.0', '0.2.99']) {
    const f = await fixture(t);
    f.manifest.version = version;
    f.manifest.installer.fileName = `BIM-Model-Viewer-${version}-Setup.exe`;
    await f.save();
    const result = await f.service.check(f.feed);
    assert.equal(result.status, 'up-to-date');
    assert.equal(result.latestVersion, version);
    assert.equal((await f.service.stage()).code, 'no-verified-update');
    assert.deepEqual(await readdir(f.userData), []);
  }
});

test('no configured feed, missing files and relative or device paths produce structured states', async t => {
  const f = await fixture(t);
  assert.equal((await f.service.check('')).status, 'not-configured');
  assert.equal((await f.service.check('relative/feed')).code, 'unsafe-path');
  assert.equal((await f.service.check('https://example.invalid/updates')).code, 'unsafe-path');
  assert.equal((await f.service.check('\\\\?\\C:\\updates')).code, 'unsafe-path');
  assert.equal((await f.service.check(path.join(f.root, 'missing'))).code, 'not-found');
  assert.equal((await f.service.verifyStaged()).code, 'not-staged');
});

test('manifest identity, channel, schema, hash and size are validated', async t => {
  const cases = [
    (v: any) => { v.productId = 'other.product'; },
    (v: any) => { v.channel = 'preview'; },
    (v: any) => { v.schemaVersion = 2; },
    (v: any) => { v.installer.sha256 = 'not-a-hash'; },
    (v: any) => { v.installer.size = -1; },
    (v: any) => { v.installer.size = 1.5; },
    (v: any) => { v.installer.size = 3 * 1024 * 1024 * 1024; },
  ];
  for (const mutate of cases) {
    const f = await fixture(t); mutate(f.manifest); await f.save();
    assert.equal((await f.service.check(f.feed)).status, 'error');
    assert.equal((await f.service.stage()).code, 'no-verified-update');
  }
});

test('installer basename must match version exactly, rejecting traversal, ADS and shell-shaped names', async t => {
  for (const name of ['../evil.exe', '..\\evil.exe', '/evil.exe', 'C:\\evil.exe', 'BIM-Model-Viewer-0.4.0-Setup.exe:payload',
    'BIM-Model-Viewer-0.4.0-Setup.exe ', 'BIM-Model-Viewer-0.5.0-Setup.exe', 'evil.exe & calc.exe',
    'Bright.Vision-0.4.0-Setup.exe:payload', 'Bright.Vision-0.4.0-Setup.exe ', 'Bright.Vision-0.5.0-Setup.exe',
    'bright.vision-0.4.0-Setup.exe', 'Bright-Vision-0.4.0-Setup.exe', 'Bright.Vision-0.4.0-Setup.exe\\evil']) {
    const f = await fixture(t); f.manifest.installer.fileName = name; await f.save();
    assert.equal((await f.service.check(f.feed)).code, 'unsafe-installer-name', name);
  }
});

test('publishing a unified package creates two independent equal copies and an old-client-compatible feed', async t => {
  const f = await fixture(t);
  const publisherUrl = new URL('../scripts/publish-update.mjs', import.meta.url).href;
  const { publishUpdate } = await import(publisherUrl);
  const installer = path.join(f.root, 'Bright.Vision-0.5.0-Setup.exe');
  await writeFile(installer, f.bytes);
  const output = path.join(f.root, 'published');
  const result = await publishUpdate({ installer, outputs: [output, output], version: '0.5.0' });
  assert.equal(result.outputs.length, 1);
  const manifest = JSON.parse(await readFile(path.join(output, 'update.json'), 'utf8'));
  assert.equal(manifest.productId, 'local.bim.modelviewer');
  assert.equal(manifest.version, '0.5.0');
  assert.equal(manifest.installer.fileName, 'BIM-Model-Viewer-0.5.0-Setup.exe');
  assert.equal(manifest.installer.sha256, hash(f.bytes));
  const [modern, legacy] = await Promise.all(installerFileNames('0.5.0').map(async (name: string) => {
    const file = path.join(output, name);
    assert.deepEqual(await readFile(file), f.bytes);
    const metadata = await lstat(file); assert.equal(metadata.nlink, 1);
    return metadata;
  }));
  assert.notEqual(modern.ino, legacy.ino, 'compatibility package must be a copy, not a hard link.');
  const service = createUpdateService({ currentVersion: '0.4.0', userDataPath: f.userData });
  assert.equal((await service.check(output)).status, 'update-available');
  assert.equal((await service.stage()).status, 'staged');
  assert.deepEqual((await readdir(output)).sort(), ['Bright.Vision-0.5.0-Setup.exe', 'BIM-Model-Viewer-0.5.0-Setup.exe', 'update.json'].sort());
});

test('publisher also upgrades a legacy-named input in place without corrupting its compatibility copy', async t => {
  const f = await fixture(t);
  const publisherUrl = new URL('../scripts/publish-update.mjs', import.meta.url).href;
  const { publishUpdate } = await import(publisherUrl);
  await publishUpdate({ installer: f.file, outputs: [f.feed], version: '0.4.0' });
  assert.deepEqual(await readFile(f.file), f.bytes);
  assert.deepEqual(await readFile(path.join(f.feed, 'Bright.Vision-0.4.0-Setup.exe')), f.bytes);
  assert.equal(JSON.parse(await readFile(path.join(f.feed, 'update.json'), 'utf8')).installer.fileName, path.basename(f.file));
  await assert.rejects(publishUpdate({ installer: f.file, outputs: [f.feed], version: '0.5.0' }), /Installer must be named/);
});

test('manifest read is bounded and malformed or invalid UTF-8 data is rejected', async t => {
  const f = await fixture(t);
  for (const value of [Buffer.from('{bad'), Buffer.from([0xff, 0xfe, 0xfd])]) {
    await writeFile(path.join(f.feed, 'update.json'), value);
    assert.equal((await f.service.check(f.feed)).code, 'invalid-manifest');
  }
  await writeFile(path.join(f.feed, 'update.json'), ' '.repeat(64 * 1024 + 1));
  assert.equal((await f.service.check(f.feed)).code, 'size-mismatch');
});

test('installer size and SHA mismatches are rejected before staging', async t => {
  const f = await fixture(t);
  await writeFile(f.file, Buffer.alloc(f.bytes.length, 0x41));
  assert.equal((await f.service.check(f.feed)).code, 'hash-mismatch');
  await writeFile(f.file, 'short');
  assert.equal((await f.service.check(f.feed)).code, 'size-mismatch');
  assert.deepEqual(await readdir(f.userData), []);
});

test('source replacement after check cannot cause a different package to be staged', async t => {
  const f = await fixture(t);
  assert.equal((await f.service.check(f.feed)).status, 'update-available');
  await writeFile(f.file, Buffer.alloc(f.bytes.length, 0x42));
  assert.equal((await f.service.stage()).code, 'hash-mismatch');
  assert.deepEqual(await readdir(path.join(f.userData, 'update-cache')), []);
});

test('returned metadata is detached from the internal verified candidate', async t => {
  const f = await fixture(t);
  const state = await f.service.check(f.feed);
  state.installer.sha256 = '0'.repeat(64);
  state.installer.fileName = 'evil.exe';
  assert.equal((await f.service.stage()).status, 'staged');
});

test('modified staged copies fail revalidation and clear the execution path', async t => {
  const f = await fixture(t); await f.service.check(f.feed);
  const staged = await f.service.stage();
  await writeFile(staged.stagedPath, Buffer.alloc(f.bytes.length, 0x43));
  const verified = await f.service.verifyStaged();
  assert.equal(verified.code, 'hash-mismatch');
  assert.equal(verified.stagedPath, undefined);
  assert.equal((await f.service.verifyStaged()).code, 'not-staged');
});

test('a subsequent failed check invalidates a previously verified source candidate', async t => {
  const f = await fixture(t); await f.service.check(f.feed);
  assert.equal((await f.service.check(path.join(f.root, 'missing'))).status, 'error');
  assert.equal((await f.service.stage()).code, 'no-verified-update');
});

test('directory junctions or symlinks in feed ancestors are rejected', async t => {
  const f = await fixture(t);
  const alias = path.join(f.root, 'feed-alias');
  await symlink(f.feed, alias, process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal((await f.service.check(alias)).code, 'unsafe-path');
});

test('cache directory junctions cannot redirect staged writes', async t => {
  const f = await fixture(t);
  const elsewhere = path.join(f.root, 'elsewhere'); await mkdir(elsewhere);
  await symlink(elsewhere, path.join(f.userData, 'update-cache'), process.platform === 'win32' ? 'junction' : 'dir');
  await f.service.check(f.feed);
  assert.equal((await f.service.stage()).code, 'unsafe-path');
  assert.deepEqual(await readdir(elsewhere), []);
});

test('hard-linked installer files are rejected', async t => {
  const f = await fixture(t);
  await link(f.file, path.join(f.root, 'external-link.exe'));
  assert.equal((await f.service.check(f.feed)).code, 'unsafe-file');
});

test('hard-linked manifest files are rejected before parsing', async t => {
  const f = await fixture(t);
  await link(path.join(f.feed, 'update.json'), path.join(f.root, 'external-manifest.json'));
  assert.equal((await f.service.check(f.feed)).code, 'unsafe-file');
});

test('a feed changed into a junction after check cannot redirect the source copy', async t => {
  const f = await fixture(t);
  await f.service.check(f.feed);
  const moved = path.join(f.root, 'moved-feed');
  await rename(f.feed, moved);
  await symlink(moved, f.feed, process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal((await f.service.stage()).code, 'unsafe-path');
  assert.deepEqual(await readdir(path.join(f.userData, 'update-cache')), []);
});

test('staged directory redirection is rejected during final verification', async t => {
  const f = await fixture(t);
  await f.service.check(f.feed);
  const staged = await f.service.stage();
  const directory = path.dirname(staged.stagedPath), moved = directory + '-moved';
  await rename(directory, moved);
  await symlink(moved, directory, process.platform === 'win32' ? 'junction' : 'dir');
  const result = await f.service.verifyStaged();
  assert.equal(result.code, 'unsafe-path');
  assert.equal(result.stagedPath, undefined);
});

test('overlapping operations return busy without losing the active verified check', async t => {
  const f = await fixture(t);
  const first = f.service.check(f.feed);
  assert.equal((await f.service.stage()).code, 'busy');
  assert.equal((await first).status, 'update-available');
  assert.equal((await f.service.stage()).status, 'staged');
});

test('configured installer limits apply to the manifest before any installer read', async t => {
  const f = await fixture(t, { maxInstallerBytes: 10 });
  assert.equal((await f.service.check(f.feed)).code, 'size-mismatch');
  assert.deepEqual(await readdir(f.userData), []);
});

test('file symlinks are rejected when the platform permits creating them', async t => {
  const f = await fixture(t);
  const real = path.join(f.root, 'real-installer.exe'); await writeFile(real, f.bytes);
  await unlink(f.file);
  try { await symlink(real, f.file, 'file'); }
  catch (error: any) { if (error.code === 'EPERM') { t.skip('File symlinks require Windows developer mode; junction cases remain covered.'); return; } throw error; }
  assert.equal((await f.service.check(f.feed)).code, 'unsafe-path');
});
