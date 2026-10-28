import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { readFile, readdir, lstat, mkdir, writeFile } from 'node:fs/promises';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
// The new Revit workbench still needs its own real-host acceptance. A prior
// exporter-engine test cannot certify the new command or every supported host.
// Unified packaging stays opt-in for explicit local preview QA.
if (process.env.BRIGHT_BUILD_PREVIEW_EXPORTER !== '1') {
  throw new Error('Unified Revit packaging remains a preview: the civil quantity workbench requires its own host acceptance. BRIGHT_BUILD_PREVIEW_EXPORTER=1 enables explicit local preview builds only.');
}
const source = path.resolve(process.env.BRIGHT_REVIT_BUNDLE || path.join(project, '../bright-revit-installer/artifacts/BV-1.7.0-final/module'));
// Output override allows isolated workspace verification without touching the
// actual generated preview directory. It never changes a Revit installation.
const target = path.resolve(process.env.BRIGHT_REVIT_OUTPUT || path.join(project, 'installer/generated/revit-plugin'));
const sourceKey = process.platform === 'win32' ? source.toLowerCase() : source;
const targetKey = process.platform === 'win32' ? target.toLowerCase() : target;
if (sourceKey === targetKey || sourceKey.startsWith(`${targetKey}${path.sep}`) || targetKey.startsWith(`${sourceKey}${path.sep}`)) throw new Error('Source and generated Revit module directories must be separate.');
async function noRedirectedAncestors(file) {
  for (let cursor = file; cursor; cursor = path.dirname(cursor)) {
    try { if ((await lstat(cursor)).isSymbolicLink()) throw new Error('Bundle paths cannot contain links or junctions.'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (path.dirname(cursor) === cursor) break;
  }
}
await noRedirectedAncestors(source); await noRedirectedAncestors(target);
// Default packaging always checks the locally reviewed lock. Rebuilding C# may
// change its bytes: use review-revit-bundle.mjs explicitly after auditing your
// own local build. A different download path never grants trust to a module.
const lockPath = path.join(project, 'installer/revit-component-lock.json');
await noRedirectedAncestors(lockPath);
const lockInfo = await lstat(lockPath);
if (!lockInfo.isFile() || lockInfo.isSymbolicLink() || lockInfo.nlink !== 1 || lockInfo.size > 65536) throw new Error('Invalid Revit component review lock.');
const lock = JSON.parse(await readFile(lockPath, 'utf8'));
const helperNames = ['bundle.json', 'Manage-BrightVisionPlugin.ps1', 'RevitHosts.ps1', 'LegacyMigration.ps1', 'BridgeMigration.ps1', 'Invoke-BrightVisionSetup.ps1', 'README.md', 'LICENSE', 'NOTICE'];
if (lock.schemaVersion !== 1 || lock.packageId !== 'BrightVision.RevitExporter' || lock.packageVersion !== '1.7.0' || lock.sourceFileCount !== 24 || lock.variantCount !== 14 || lock.moduleFileCount !== 65 || !lock.helpers || Object.keys(lock.helpers).length !== helperNames.length || helperNames.some(name => !/^[a-f0-9]{64}$/.test(lock.helpers[name]))) throw new Error('Unexpected Revit component review lock; explicitly review the local build first.');
const helpers = new Map(helperNames.map(name => [name, lock.helpers[name]]));
const hash = data => createHash('sha256').update(data).digest('hex');
const snapshots = new Map();
async function inventory(root, relative = '') {
  const files = [];
  const info = await lstat(path.join(root, relative));
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error(`Bundle directory must not be a link: ${relative}`);
  for (const entry of await readdir(path.join(root, relative), { withFileTypes: true })) {
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...await inventory(root, name));
    else if (entry.isFile() && !entry.isSymbolicLink()) {
      if ((await lstat(path.join(root, name))).nlink !== 1) throw new Error(`Unsupported hard-linked bundle entry: ${name}`);
      files.push(name);
    }
    else throw new Error(`Unsupported bundle entry: ${name}`);
  }
  return files;
}
// Inventory before following entries, so links are rejected before reads.
const sourceFiles = await inventory(source);
const manifestBytes = await readFile(path.join(source, 'bundle.json')).catch(() => {
  throw new Error('Build the source exporter and bright-revit-installer first, or set BRIGHT_REVIT_BUNDLE to the built module directory.');
});
if (manifestBytes.length > 1048576) throw new Error('Revit bundle manifest exceeds its size limit.');
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (manifest.schemaVersion !== 2 || manifest.packageId !== 'BrightVision.RevitExporter' || manifest.packageVersion !== '1.7.0' || !Array.isArray(manifest.variants) || manifest.variants.length !== 14) throw new Error('Unexpected Revit component identity or variant count.');
const expected = new Set(helpers.keys()), variants = new Set();
for (const [name, expectedHash] of helpers) {
  const bytes = name === 'bundle.json' ? manifestBytes : await readFile(path.join(source, name));
  if (hash(bytes) !== expectedHash) throw new Error(`Revit helper verification failed: ${name}`);
  snapshots.set(name, bytes);
}
for (const variant of manifest.variants) {
  if (!/^(201[5-9]|202[0-6])-(net45|net48|net8|net10)$/.test(variant.id) || variant.id !== `${variant.year}-${variant.runtime}` || variants.has(variant.id)) throw new Error('Invalid/duplicate Revit variant.');
  variants.add(variant.id);
  if (!Array.isArray(variant.files) || variant.files.length !== 4) throw new Error('Unexpected Revit payload file count.');
  const names = new Set();
  for (const file of variant.files) {
    if (!['Bright.Vision.Revit.dll', 'Bright.Vision.addin', 'Bright.Vision.LICENSE', 'Bright.Vision.NOTICE'].includes(file.name) || names.has(file.name)) throw new Error('Unexpected Revit payload filename.');
    if (!Number.isSafeInteger(file.size) || file.size < 1 || file.size > 16777216 || !/^[a-f0-9]{64}$/.test(file.sha256)) throw new Error('Invalid Revit payload size or hash.');
    names.add(file.name);
    const name = `payload/${variant.id}/${file.name}`;
    expected.add(name);
    const data = await readFile(path.join(source, name));
    if (data.length !== file.size || hash(data) !== file.sha256) throw new Error(`Revit payload verification failed: ${name}`);
    snapshots.set(name, data);
  }
}
for (let year = 2015; year <= 2026; year++) {
  for (const runtime of year >= 2025 ? ['net8', 'net10'] : [year >= 2021 ? 'net48' : 'net45']) {
    if (!variants.has(`${year}-${runtime}`)) throw new Error('A supported Revit target is missing.');
  }
}
if (sourceFiles.length !== expected.size || sourceFiles.some(name => !expected.has(name))) throw new Error('Unexpected source module files; only the verified module may be bundled.');
try {
  const oldFiles = await inventory(target);
  if (oldFiles.some(name => !expected.has(name))) throw new Error('Generated module has unknown files; use a clean generated directory.');
} catch (error) { if (error.code !== 'ENOENT') throw error; }
// Copy the same bytes that passed verification, avoiding a second source read.
for (const [name, bytes] of snapshots) {
  const destination = path.join(target, name);
  await mkdir(path.dirname(destination), { recursive: true });
  await writeFile(destination, bytes);
  if (hash(await readFile(destination)) !== hash(bytes)) throw new Error(`Bundled component copy failed: ${name}`);
}
console.log(`Prepared bright.vision preview Revit component ${manifest.packageVersion}: ${variants.size} variants, ${sourceFiles.length} files.`);
