import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { prepareGitHubRelease, verifyPreparedRelease } from '../prepare-github-release.mjs';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const testRoot = path.join(project, 'test-results');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
async function fixture(t, product = 'viewer') {
  await mkdir(testRoot, { recursive: true });
  const directory = await mkdtemp(path.join(testRoot, 'prepare-release-'));
  t.after(async () => { assert.ok(path.resolve(directory).startsWith(path.resolve(testRoot) + path.sep)); await rm(directory, { recursive: true, force: true }); });
  const version = product === 'viewer' ? '0.6.0' : '1.7.0';
  const installer = path.join(directory, product === 'viewer' ? `Bright.Vision-${version}-Setup.exe` : `BV-${version}-Setup.exe`);
  const bytes = Buffer.from('MZ inert release preparation fixture, never executed\n');
  await writeFile(installer, bytes);
  return { directory, bytes, options: { repository: 'BrightVision/bright-vision', product, channel: 'preview', version, installer, outputDirectory: path.join(directory, 'prepared') } };
}
test('viewer preparation makes a company-attributed schema2 manifest and exact release assets offline', async t => {
  const { options, bytes } = await fixture(t);
  const result = await prepareGitHubRelease({ ...options, revitExporterVersion: '1.7.0' });
  assert.equal(result.manifest.publisher, '崛亮视觉'); assert.equal(result.manifest.productId, 'local.bim.modelviewer');
  assert.equal(result.manifest.installer.url, 'https://github.com/BrightVision/bright-vision/releases/download/viewer-v0.6.0/Bright.Vision-0.6.0-Setup.exe');
  assert.equal(result.manifest.installer.size, bytes.length); assert.equal(result.manifest.installer.sha256, sha(bytes));
  assert.equal(result.manifest.components.revitExporterVersion, '1.7.0'); assert.equal(result.descriptor.publicReleaseCreated, false);
  const checked = await verifyPreparedRelease(options.outputDirectory); assert.equal(checked.installer.sha256, sha(bytes));
  assert.deepEqual(await readFile(path.join(options.outputDirectory, result.manifest.installer.fileName)), bytes);
  const publicText = await readFile(path.join(options.outputDirectory, 'update.json'), 'utf8');
  assert.ok(!publicText.includes(options.installer)); assert.ok(!publicText.includes('H:\\'));
});
test('standalone Revit package has separate product identity, tag and filename', async t => {
  const { options } = await fixture(t, 'revit'); const result = await prepareGitHubRelease(options);
  assert.equal(result.manifest.productId, 'BrightVision.RevitExporter'); assert.equal(result.descriptor.tag, 'revit-v1.7.0');
  assert.equal(result.manifest.installer.fileName, 'BV-1.7.0-Setup.exe'); assert.equal(result.manifest.components, undefined);
  await verifyPreparedRelease(options.outputDirectory);
});
test('URL/repository traversal, nonnumeric version, unknown channel/product and mismatched executable names reject', async t => {
  const { options } = await fixture(t);
  for (const patch of [{ repository: 'https://github.com/a/b' }, { repository: 'owner/../repo' }, { repository: 'owner/repo.git' }, { repository: 'bad--owner/repo' },
    { version: '0.6.0-preview' }, { version: '00.6.0' }, { channel: 'nightly' }, { product: 'cloud' }, { installer: path.join(path.dirname(options.installer), 'Other.exe') },
    { outputDirectory: 'relative/path' }]) await assert.rejects(prepareGitHubRelease({ ...options, ...patch }));
});
test('preparation never overwrites a reviewed directory or silently mutates the input bytes', async t => {
  const { options, bytes } = await fixture(t); await prepareGitHubRelease(options);
  await assert.rejects(prepareGitHubRelease(options), /already exists/);
  assert.deepEqual(await readFile(options.installer), bytes);
});
test('digest/checksum disagreement, foreign repo and unexpected descriptor assets reject before publishing', async t => {
  const { options } = await fixture(t); const result = await prepareGitHubRelease(options);
  const manifestPath = path.join(options.outputDirectory, 'update.json'), descriptorPath = path.join(options.outputDirectory, 'release-descriptor.json');
  await writeFile(manifestPath, JSON.stringify({ ...result.manifest, installer: { ...result.manifest.installer, sha256: '0'.repeat(64) } }));
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /hash/);
  await writeFile(manifestPath, JSON.stringify({ ...result.manifest, installer: { ...result.manifest.installer, url: 'https://evil.invalid/file.exe' } }));
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /GitHub URL/);
  await writeFile(manifestPath, JSON.stringify(result.manifest));
  await writeFile(descriptorPath, JSON.stringify({ ...result.descriptor, assets: ['arbitrary.ps1'] }));
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /unexpected assets/);
});
test('tampered staged installer fails independently of descriptor and release notes', async t => {
  const { options } = await fixture(t); const result = await prepareGitHubRelease(options);
  await writeFile(path.join(options.outputDirectory, result.manifest.installer.fileName), Buffer.from('MZ tampered payload'));
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /hash/);
});

test('publish verification rejects command fields even if a modified manifest has matching checksums', async t => {
  const { options } = await fixture(t); const result = await prepareGitHubRelease(options);
  const manifest = { ...result.manifest, command: 'inert untrusted command' };
  const bytes = Buffer.from(JSON.stringify(manifest));
  await writeFile(path.join(options.outputDirectory, 'update.json'), bytes);
  await writeFile(path.join(options.outputDirectory, 'SHA256SUMS.txt'), `${result.manifest.installer.sha256}  ${result.manifest.installer.fileName}\n${sha(bytes)}  update.json\n`);
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /Unexpected manifest fields/);
});
test('manifest, descriptor, notes and checksum reads have bounded sizes', async t => {
  const { options } = await fixture(t); await prepareGitHubRelease(options);
  const file = path.join(options.outputDirectory, 'release-descriptor.json'); await writeFile(file, Buffer.alloc(65537));
  await assert.rejects(verifyPreparedRelease(options.outputDirectory), /64 KiB/);
});
test('non-PE and symbolic-link installers are not accepted as release artifacts', async t => {
  const { options, directory } = await fixture(t); await writeFile(options.installer, Buffer.from('not a PE installer'));
  await assert.rejects(prepareGitHubRelease(options), /PE installer/);
  await writeFile(options.installer, Buffer.from('MZ inert symlink fixture'));
  const link = path.join(directory, 'linked'); await mkdir(link);
  const linkedInstaller = path.join(link, path.basename(options.installer));
  try { await symlink(options.installer, linkedInstaller, 'file'); }
  catch (error) { if (error.code === 'EPERM' || error.code === 'EACCES') { t.skip('OS does not permit file symlinks in this test context.'); return; } throw error; }
  await assert.rejects(prepareGitHubRelease({ ...options, installer: linkedInstaller }), /Symlink/);
});
