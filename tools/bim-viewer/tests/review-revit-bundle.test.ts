import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
// @ts-expect-error This build-time ESM module is executed directly by Node.
import { reviewRevitBundle } from '../scripts/review-revit-bundle.mjs';

const workspace = path.resolve(fileURLToPath(new URL('../test-results/', import.meta.url)));
const hash = (bytes: Uint8Array) => createHash('sha256').update(bytes).digest('hex');
async function fixture(t: TestContext) {
  await mkdir(workspace, { recursive: true });
  const root = await mkdtemp(path.join(workspace, 'review-revit-'));
  t.after(async () => { assert.equal(path.dirname(root), workspace); await rm(root, { recursive: true, force: true }); });
  const moduleDirectory = path.join(root, 'module'), installerSourceDirectory = path.join(root, 'installer'), exporterSourceDirectory = path.join(root, 'exporter'), legalDirectory = path.join(root, 'legal');
  for (const directory of [moduleDirectory, installerSourceDirectory, exporterSourceDirectory, legalDirectory]) await mkdir(directory);
  const put = async (relative: string, bytes: Uint8Array) => { const file = path.join(moduleDirectory, relative); await mkdir(path.dirname(file), { recursive: true }); await writeFile(file, bytes); return { name: path.basename(file), size: bytes.length, sha256: hash(bytes) }; };
  for (const name of ['Manage-BrightVisionPlugin.ps1','RevitHosts.ps1','LegacyMigration.ps1','BridgeMigration.ps1','Invoke-BrightVisionSetup.ps1','README.md']) {
    const bytes = Buffer.from(`INERT OWNED HELPER ${name}`); await writeFile(path.join(installerSourceDirectory, name), bytes); await put(name, bytes);
  }
  const legal: Record<string, Buffer> = {};
  for (const name of ['LICENSE','NOTICE']) { legal[name] = Buffer.from(`INERT COMPANY ${name}`); await writeFile(path.join(legalDirectory, name), legal[name]); await put(name, legal[name]); }
  const sourceFiles = [];
  for (let index = 0; index < 24; index++) { const name = `Source${index}.cs`, bytes = Buffer.from(`INERT SOURCE ${index}`); await writeFile(path.join(exporterSourceDirectory, name), bytes); sourceFiles.push({ name, sha256: hash(bytes) }); }
  const variants = [], producerVariants = [];
  for (let year = 2015; year <= 2026; year++) for (const runtime of year >= 2025 ? ['net8','net10'] : [year >= 2021 ? 'net48' : 'net45']) {
    const id = `${year}-${runtime}`, relative = `payload/${id}/`, dll = await put(relative + 'Bright.Vision.Revit.dll', Buffer.from(`INERT DLL ${id}`));
    const addin = Buffer.from('<FullClassName>BrightVision.Revit.App</FullClassName><Assembly>Bright.Vision.Revit.dll</Assembly><VendorDescription>崛亮视觉 · Bright Vision</VendorDescription><AddInId>6F12FA44-368B-4E47-9197-3DE316D5392A</AddInId>');
    const files = [dll, await put(relative + 'Bright.Vision.addin', addin), await put(relative + 'Bright.Vision.LICENSE', legal.LICENSE), await put(relative + 'Bright.Vision.NOTICE', legal.NOTICE)];
    variants.push({ id, year: String(year), runtime, files }); producerVariants.push({ id, buildStatus: 'built', files: [dll] });
  }
  await put('bundle.json', Buffer.from(JSON.stringify({ schemaVersion: 2, packageId: 'BrightVision.RevitExporter', packageVersion: '1.7.0', variants })));
  const buildManifestPath = path.join(root, 'exporter-build-manifest.json');
  await writeFile(buildManifestPath, JSON.stringify({ packageId: 'BrightVision.RevitExporter', packageVersion: '1.7.0', sourceFiles, variants: producerVariants }));
  return { root, moduleDirectory, installerSourceDirectory, exporterSourceDirectory, legalDirectory, buildManifestPath, outputLock: path.join(root, 'review.json'), acknowledgeReviewedOwnedSource: true };
}

test('explicit local review verifies all 24 sources, 14 targets, 65 files and preserves complete helper pins', async t => {
  const options = await fixture(t), result = await reviewRevitBundle(options);
  assert.equal(result.lock.variantCount, 14); assert.equal(result.lock.moduleFileCount, 65); assert.equal(Object.keys(result.lock.helpers).length, 9);
  assert.equal(result.lock.sourceFileCount, 24); assert.match(result.lock.reviewScope, /not an attestation/);
  assert.deepEqual(JSON.parse(await readFile(options.outputLock, 'utf8')), result.lock);
  await reviewRevitBundle(options); // Explicit re-review may replace this tool's lock.
});
test('review requires an explicit acknowledgement and never writes a lock implicitly', async t => {
  const options = await fixture(t);
  await assert.rejects(reviewRevitBundle({ ...options, acknowledgeReviewedOwnedSource: false }), /acknowledge-reviewed-owned-source/);
  await assert.rejects(readFile(options.outputLock), { code: 'ENOENT' });
});
test('changed current exporter sources or helpers cannot grant trust to an old build manifest', async t => {
  const source = await fixture(t); await writeFile(path.join(source.exporterSourceDirectory, 'Source0.cs'), 'CHANGED');
  await assert.rejects(reviewRevitBundle(source), /source differs/);
  const helper = await fixture(t); await writeFile(path.join(helper.moduleDirectory, 'Manage-BrightVisionPlugin.ps1'), 'CHANGED');
  await assert.rejects(reviewRevitBundle(helper), /helper differs/);
});
test('corrupt payload, changed legal attribution and additional files are rejected', async t => {
  const payload = await fixture(t); await writeFile(path.join(payload.moduleDirectory, 'payload/2020-net45/Bright.Vision.Revit.dll'), 'CHANGED');
  await assert.rejects(reviewRevitBundle(payload), /size\/hash mismatch/);
  const legal = await fixture(t); await writeFile(path.join(legal.moduleDirectory, 'NOTICE'), 'CHANGED');
  await assert.rejects(reviewRevitBundle(legal), /license\/NOTICE differs/);
  const extra = await fixture(t); await writeFile(path.join(extra.moduleDirectory, 'extra.exe'), 'INERT EXTRA');
  await assert.rejects(reviewRevitBundle(extra), /exactly 65/);
});
test('review refuses to overwrite an unrelated existing file', async t => {
  const options = await fixture(t); await writeFile(options.outputLock, '{"private":"preserve"}');
  await assert.rejects(reviewRevitBundle(options), /unrelated/); assert.equal(await readFile(options.outputLock, 'utf8'), '{"private":"preserve"}');
});
