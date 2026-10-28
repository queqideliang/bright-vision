import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';
import { readFile, readdir, lstat, mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import * as ResEdit from 'resedit';

const require = createRequire(import.meta.url);
const asar = require('@electron/asar');
const project = process.cwd();
const projectVersion = JSON.parse(await readFile(path.join(project, 'package.json'), 'utf8')).version;
const expectedViewerVersion = process.argv[5] || projectVersion;
const expectedExporterVersion = process.argv[4] || '1.7.0';
if (!/^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$/.test(expectedViewerVersion) || !/^\d+\.\d+\.\d+$/.test(expectedExporterVersion)) throw new Error('Expected explicit viewer and exporter semantic versions.');
const installer = path.resolve(process.argv[2] || `release/open-source-preview/Bright.Vision-${expectedViewerVersion}-Setup.exe`);
const frozen = path.resolve(process.argv[3] || `../bright-revit-installer/artifacts/BV-${expectedExporterVersion}-final/module`);
const sevenZip = process.env.BRIGHT_TEST_7ZA;
if (!sevenZip || !path.isAbsolute(sevenZip)) throw new Error('Set BRIGHT_TEST_7ZA to the existing absolute 7za.exe tool path.');
const resultRoot = path.join(project, 'test-results');
await mkdir(resultRoot, { recursive: true });
const result = await mkdtemp(path.join(resultRoot, 'civil-package-'));
assert.equal(path.dirname(result), resultRoot);
// The target is our just-built package. Extract only resources, never execute its installer.
await promisify(execFile)(sevenZip, ['x', installer, 'resources/*', `-o${result}`, '-y', '-bso0', '-bsp0'], { windowsHide: true, maxBuffer: 1024 * 1024 });
const resources = path.join(result, 'resources');
const embedded = path.join(resources, 'revit-plugin');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
let assertions = 0;
function equal(actual, expected, label) { assert.deepEqual(actual, expected, label); assertions++; }
async function files(root, relative = '') {
  const names = [];
  for (const entry of await readdir(path.join(root, relative), { withFileTypes: true })) {
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    assert.equal((await lstat(path.join(root, name))).isSymbolicLink(), false);
    if (entry.isDirectory()) names.push(...await files(root, name));
    else if (entry.isFile()) names.push(name);
    else throw new Error(`Unsupported package resource ${name}`);
  }
  return names.sort();
}
const expected = await files(frozen);
equal(await files(embedded), expected, 'final EXE must contain exactly the frozen module');
for (const name of expected) equal(digest(await readFile(path.join(embedded, name))), digest(await readFile(path.join(frozen, name))), `embedded ${name}`);
const manifest = JSON.parse(await readFile(path.join(embedded, 'bundle.json'), 'utf8'));
equal(manifest.packageVersion, expectedExporterVersion, 'embedded exporter version');
equal(manifest.variants.length, 14, 'embedded exporter variants');
const integration = 'revit-integration/Install-RevitIntegration.ps1';
equal(digest(await readFile(path.join(resources, integration))), digest(await readFile('installer/Install-RevitIntegration.ps1')), 'embedded integration script');
const archive = path.join(resources, 'app.asar');
equal(digest(await readFile(archive)), digest(await readFile(path.join(path.dirname(installer), 'win-unpacked/resources/app.asar'))), 'EXE app archive matches unpacked build');
equal(JSON.parse(asar.extractFile(archive, 'package.json').toString()).version, expectedViewerVersion, 'packaged viewer version');
const dist = await files(path.resolve('dist'));
for (const name of dist) equal(digest(asar.extractFile(archive, path.join('dist', name))), digest(await readFile(path.join('dist', name))), `packaged renderer ${name}`);
equal(digest(asar.extractFile(archive, path.join('electron', 'revit-integration.cjs'))), digest(await readFile('electron/revit-integration.cjs')), 'packaged integration bridge');
if (expectedViewerVersion === '0.6.0') {
  equal(expected.length, 65, 'legal-aware BV 1.7 module file count');
  for (const name of ['main.cjs', 'preload.cjs', 'desktop-updates.cjs', 'github-update-source.cjs', 'update-release.json', 'assets/bv.ico'])
    equal(digest(asar.extractFile(archive, path.join('electron', name))), digest(await readFile(path.join('electron', name))), `packaged official source/brand ${name}`);
  const configuration = JSON.parse(asar.extractFile(archive, 'electron/update-release.json').toString());
  equal(configuration, JSON.parse(await readFile('electron/update-release.json', 'utf8')), 'packaged official source matches reviewed build configuration');
  equal(configuration.enabled, true, 'confirmed official repository enabled');
  equal(configuration.owner, 'queqideliang', 'fixed company-approved owner');
  equal(configuration.repository, 'bright-vision', 'fixed company-approved repository');
  equal(configuration.channel, 'preview', 'preview subscription keeps stable users separate');
  for (const name of ['LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md']) {
    const expectedHash = digest(await readFile(name));
    equal(digest(await readFile(path.join(resources, 'attribution', name))), expectedHash, `distributed company/third-party ${name}`);
    // electron-builder omits root inputs already copied by extraResources.
    // The Web license copy is retained in app.asar as well as attribution/.
    equal(digest(asar.extractFile(archive, path.join('dist/licenses', name))), expectedHash, `app archive company/third-party ${name}`);
  }
  const native = path.join(resources, 'native-helper');
  equal(await files(native), ['ArchiveExtractor.exe', 'manifest.json'], 'only the fixed helper and manifest are shipped');
  const helperManifest = JSON.parse(await readFile(path.join(native, 'manifest.json'), 'utf8'));
  const helperBytes = await readFile(path.join(native, 'ArchiveExtractor.exe'));
  equal(helperManifest.productVersion, expectedViewerVersion, 'fixed helper version matches viewer');
  equal(helperManifest.executable.bytes, helperBytes.length, 'fixed helper exact size');
  equal(helperManifest.executable.sha256, digest(helperBytes), 'fixed helper exact hash');
  equal(digest(helperBytes), digest(await readFile('native/generated/ArchiveExtractor.exe')), 'same genuinely accepted local helper is shipped');
  const executable = path.join(path.dirname(installer), 'win-unpacked/BIM Model Viewer.exe');
  const pe = ResEdit.NtExecutable.from(await readFile(executable), { ignoreCert: true });
  const resourceEntries = ResEdit.NtExecutableResource.from(pe).entries;
  const versionEntries = ResEdit.Resource.VersionInfo.fromEntries(resourceEntries);
  assert.ok(versionEntries.length > 0); assertions++;
  const info = versionEntries[0].getStringValues(versionEntries[0].getAllLanguagesForStringValues()[0]);
  equal(info.CompanyName, '崛亮视觉', 'actual Windows executable company resource');
  equal(info.ProductName, 'Bright Vision', 'actual Windows executable product resource');
  assert.match(info.LegalCopyright, /崛亮视觉/); assertions++;
  assert.match(info.ProductVersion, /^0\.6\.0(?:\.0)?$/); assertions++;
  const expectedIcons = ResEdit.Data.IconFile.from(await readFile('electron/assets/bv.ico')).icons.map(item => digest(Buffer.from(item.data.generate()))).sort();
  const iconGroups = ResEdit.Resource.IconGroupEntry.fromEntries(resourceEntries);
  assert.ok(iconGroups.some(group => {
    const icons = group.getIconItemsFromEntries(resourceEntries);
    return icons.length === expectedIcons.length && icons.every(item => typeof item.generate === 'function')
      && JSON.stringify(icons.map(item => digest(Buffer.from(item.generate()))).sort()) === JSON.stringify(expectedIcons);
  }), 'actual Windows executable embeds all six owned BV icon bitmaps'); assertions++;
}
const report = { result: 'PASS', assertions, verificationScope: 'Extracted NSIS resources and app archive; no installer execution or Revit host test',
  localPreview: true, viewerVersion: expectedViewerVersion, exporterVersion: manifest.packageVersion, variants: manifest.variants.length,
  moduleFiles: expected.length, installer, installerBytes: (await lstat(installer)).size, installerSha256: digest(await readFile(installer)),
  extractedResources: resources, liveRevitExportVerified: false, nsisInstallationExecuted: false };
await writeFile(path.join(result, 'report.json'), JSON.stringify(report, null, 2));
await writeFile(path.join(resultRoot, 'civil-package-latest.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report));
