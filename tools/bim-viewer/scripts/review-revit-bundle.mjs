import { createHash, randomUUID } from 'node:crypto';
import { lstat, readFile, readdir, realpath, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const project = fileURLToPath(new URL('../', import.meta.url));
const helpers = ['Manage-BrightVisionPlugin.ps1', 'RevitHosts.ps1', 'LegacyMigration.ps1', 'BridgeMigration.ps1', 'Invoke-BrightVisionSetup.ps1', 'README.md'];
const legalNames = ['LICENSE', 'NOTICE'];
const payloadNames = ['Bright.Vision.Revit.dll', 'Bright.Vision.addin', 'Bright.Vision.LICENSE', 'Bright.Vision.NOTICE'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (a, b) => process.platform === 'win32' ? path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase() : path.resolve(a) === path.resolve(b);
async function plain(file, optional = false) {
  const full = path.resolve(file);
  for (let current = full; current; current = path.dirname(current)) {
    try { if ((await lstat(current)).isSymbolicLink()) throw new Error('Reviewed bundle paths cannot contain links or junctions.'); }
    catch (error) { if (!optional || error.code !== 'ENOENT' || current !== full) throw error; }
    if (path.dirname(current) === current) break;
  }
  if (!optional && !samePath(await realpath(full), full)) throw new Error('Reviewed bundle path was redirected.');
  return full;
}
async function bytes(file, limit = 1024 * 1024) {
  await plain(file); const info = await lstat(file);
  if (!info.isFile() || info.nlink !== 1 || info.size < 1 || info.size > limit) throw new Error('Review input must be a bounded, singly linked regular file.');
  const result = await readFile(file); if (result.length !== info.size) throw new Error('Review input changed during reading.'); return result;
}
async function inventory(directory, relative = '') {
  const files = []; await plain(path.join(directory, relative));
  for (const entry of await readdir(path.join(directory, relative), { withFileTypes: true })) {
    const name = relative ? relative + '/' + entry.name : entry.name;
    if (entry.isSymbolicLink()) throw new Error('Unexpected link in reviewed bundle.');
    if (entry.isDirectory()) files.push(...await inventory(directory, name));
    else if (entry.isFile()) files.push(name); else throw new Error('Unexpected entry in reviewed bundle.');
  }
  return files;
}

// Explicit developer review of a freshly LOCAL build. A build manifest is a
// claim, not a compiler attestation: do not run this on an arbitrary downloaded
// DLL and infer that it was compiled from the matching source hashes.
export async function reviewRevitBundle({ moduleDirectory, buildManifestPath, outputLock = path.join(project, 'installer/revit-component-lock.json'),
  acknowledgeReviewedOwnedSource = false, installerSourceDirectory = path.resolve(project, '../bright-revit-installer'),
  exporterSourceDirectory = path.resolve(project, '../bright-revit-exporter'), legalDirectory = path.resolve(project, '../bright-open-source/legal') } = {}) {
  if (!acknowledgeReviewedOwnedSource) throw new Error('Explicit --acknowledge-reviewed-owned-source is required after reviewing your fresh local build.');
  if (typeof moduleDirectory !== 'string' || !path.isAbsolute(moduleDirectory)) throw new Error('An absolute local module directory is required.');
  const module = await plain(moduleDirectory), producerPath = await plain(buildManifestPath || path.join(path.dirname(module), 'exporter-build-manifest.json'));
  const producerBytes = await bytes(producerPath), producer = JSON.parse(producerBytes);
  if (producer.packageId !== 'BrightVision.RevitExporter' || producer.packageVersion !== '1.7.0' || !Array.isArray(producer.variants) || producer.variants.length !== 14 || !Array.isArray(producer.sourceFiles) || producer.sourceFiles.length !== 24) throw new Error('Review requires the BV 1.7.0 / 24-source / 14-target local producer manifest.');
  const sourceNames = new Set();
  for (const source of producer.sourceFiles) {
    if (!/^[A-Za-z0-9_.-]+\.cs$/.test(source.name) || sourceNames.has(source.name) || !/^[a-f\d]{64}$/.test(source.sha256)) throw new Error('Invalid/duplicate local producer source identity.');
    sourceNames.add(source.name); if (hash(await bytes(path.join(exporterSourceDirectory, source.name))) !== source.sha256) throw new Error('Current exporter source differs from the local compiled source snapshot.');
  }
  const files = await inventory(module), manifestBytes = await bytes(path.join(module, 'bundle.json')), manifest = JSON.parse(manifestBytes);
  if (manifest.schemaVersion !== 2 || manifest.packageId !== producer.packageId || manifest.packageVersion !== '1.7.0' || !Array.isArray(manifest.variants) || manifest.variants.length !== 14) throw new Error('Unexpected BV 1.7.0 module manifest.');
  const records = {}, expected = new Set(['bundle.json', ...helpers, ...legalNames]); records['bundle.json'] = hash(manifestBytes);
  for (const name of helpers) {
    const actual = await bytes(path.join(module, name)), owned = await bytes(path.join(installerSourceDirectory, name));
    if (hash(actual) !== hash(owned)) throw new Error(`Module helper differs from the reviewed current owned source: ${name}`); records[name] = hash(actual);
  }
  const legal = {};
  for (const name of legalNames) {
    legal[name] = await bytes(path.join(legalDirectory, name)); const actual = await bytes(path.join(module, name));
    if (hash(actual) !== hash(legal[name])) throw new Error('Module license/NOTICE differs from the canonical company distribution text.'); records[name] = hash(actual);
  }
  const targets = new Set();
  for (const variant of manifest.variants) {
    if (!/^(201[5-9]|202[0-6])-(net45|net48|net8|net10)$/.test(variant.id) || variant.id !== `${variant.year}-${variant.runtime}` || targets.has(variant.id)) throw new Error('Invalid/duplicate runtime variant.'); targets.add(variant.id);
    const compiled = producer.variants.filter(item => item.id === variant.id && item.buildStatus === 'built');
    if (compiled.length !== 1 || !Array.isArray(variant.files) || variant.files.length !== 4) throw new Error('Expected a compiled target with four owned payload files.');
    const names = new Set();
    for (const file of variant.files) {
      if (!payloadNames.includes(file.name) || names.has(file.name) || !Number.isSafeInteger(file.size) || file.size < 1 || file.size > 16777216 || !/^[a-f\d]{64}$/.test(file.sha256)) throw new Error('Invalid payload entry.'); names.add(file.name);
      const relative = `payload/${variant.id}/${file.name}`; expected.add(relative); const data = await bytes(path.join(module, relative), 16777216);
      if (data.length !== file.size || hash(data) !== file.sha256) throw new Error('Module payload size/hash mismatch.');
      if (file.name === 'Bright.Vision.Revit.dll') {
        const claim = compiled[0].files.filter(item => item.name === file.name);
        if (claim.length !== 1 || claim[0].sha256 !== file.sha256 || claim[0].size !== file.size) throw new Error('DLL bytes differ from the locally built producer manifest.');
      } else if (file.name === 'Bright.Vision.LICENSE' && hash(data) !== hash(legal.LICENSE) || file.name === 'Bright.Vision.NOTICE' && hash(data) !== hash(legal.NOTICE)) throw new Error('Per-variant legal attribution is missing or changed.');
      else if (file.name === 'Bright.Vision.addin') {
        const xml = data.toString('utf8');
        if (!xml.includes('<FullClassName>BrightVision.Revit.App</FullClassName>') || !xml.includes('<Assembly>Bright.Vision.Revit.dll</Assembly>') || !xml.includes('<VendorDescription>崛亮视觉 · Bright Vision</VendorDescription>') || !xml.includes('<AddInId>6F12FA44-368B-4E47-9197-3DE316D5392A</AddInId>')) throw new Error('Unexpected addin identity or company attribution.');
      }
    }
  }
  for (let year = 2015; year <= 2026; year++) for (const runtime of year >= 2025 ? ['net8', 'net10'] : [year >= 2021 ? 'net48' : 'net45']) if (!targets.has(`${year}-${runtime}`)) throw new Error('A supported compile target is missing.');
  if (expected.size !== 65 || files.length !== expected.size || files.some(name => !expected.has(name))) throw new Error('Unexpected module files; reviewed BV 1.7.0 must have exactly 65 files.');
  const lock = { schemaVersion: 1, packageId: manifest.packageId, packageVersion: manifest.packageVersion, sourceFileCount: producer.sourceFiles.length, variantCount: targets.size, moduleFileCount: files.length,
    producerManifestSha256: hash(producerBytes), sourceFiles: producer.sourceFiles, helpers: records,
    reviewScope: 'Explicitly reviewed local build; source/helper/legal/payload consistency checked. This is not an attestation for arbitrary downloaded binaries or a bit-for-bit reproducibility claim.' };
  const output = path.resolve(outputLock); await plain(path.dirname(output)); await plain(output, true);
  try { const info = await lstat(output); if (!info.isFile() || info.nlink !== 1) throw new Error('Existing review lock is not an ordinary owned file.'); const old = JSON.parse(await bytes(output)); if (old.schemaVersion !== 1 || old.packageId !== 'BrightVision.RevitExporter') throw new Error('Refusing to replace an unrelated file.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const temporary = path.join(path.dirname(output), `.revit-component-review-${randomUUID()}.tmp`);
  await writeFile(temporary, JSON.stringify(lock, null, 2) + '\n', { flag: 'wx' });
  await rename(temporary, output); return { outputLock: output, lock };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = {};
    for (let index = 2; index < process.argv.length; index++) {
      const key = process.argv[index];
      if (key === '--acknowledge-reviewed-owned-source') options.acknowledgeReviewedOwnedSource = true;
      else if (key === '--bundle' && process.argv[index + 1]) options.moduleDirectory = path.resolve(process.argv[++index]);
      else if (key === '--exporter-build-manifest' && process.argv[index + 1]) options.buildManifestPath = path.resolve(process.argv[++index]);
      else if (key === '--output-lock' && process.argv[index + 1]) options.outputLock = path.resolve(process.argv[++index]);
      else throw new Error('Usage: node scripts/review-revit-bundle.mjs --bundle LOCAL_MODULE [--exporter-build-manifest FILE] [--output-lock FILE] --acknowledge-reviewed-owned-source');
    }
    const result = await reviewRevitBundle(options); console.log(JSON.stringify({ reviewedLocalBundle: true, outputLock: result.outputLock, version: result.lock.packageVersion, variants: result.lock.variantCount, files: result.lock.moduleFileCount, sourceFiles: result.lock.sourceFileCount, published: false }));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
