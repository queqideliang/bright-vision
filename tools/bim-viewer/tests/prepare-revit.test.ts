import assert from 'node:assert/strict';
import test, { type TestContext } from 'node:test';
import { existsSync } from 'node:fs';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { cp, link, mkdir, mkdtemp, readFile, readdir, rm, stat, symlink, unlink, writeFile } from 'node:fs/promises';

const run = promisify(execFile);
const project = fileURLToPath(new URL('../', import.meta.url));
const script = path.join(project, 'scripts/prepare-revit.mjs');
const frozen = path.resolve(process.env.BRIGHT_REVIT_TEST_BUNDLE || path.join(project, '../bright-revit-installer/artifacts/BV-1.7.0-final/module'));
// Public source CI has no redistributed Autodesk SDK or compiled module. Only
// these integration fixtures skip there. A present local module runs every
// check; malformed or changed modules remain failures, never skips.
const integrationTest = (name: string, callback: (context: TestContext) => Promise<void>) =>
  test(name, { skip: existsSync(frozen) ? false : 'Reviewed Revit module absent: build with local licensed API references, explicitly review it, then run these integration checks.' }, callback);
const workspace = path.join(project, 'test-results');
const digest = (data: Uint8Array) => createHash('sha256').update(data).digest('hex');
const helperNames = ['bundle.json', 'Manage-BrightVisionPlugin.ps1', 'RevitHosts.ps1', 'LegacyMigration.ps1', 'BridgeMigration.ps1', 'Invoke-BrightVisionSetup.ps1', 'README.md', 'LICENSE', 'NOTICE'];

async function fixture(t: { after(callback: () => Promise<void>): void }, clone = true) {
  await mkdir(workspace, { recursive: true });
  const root = await mkdtemp(path.join(workspace, 'prepare-revit-'));
  t.after(async () => {
    assert.equal(path.dirname(root), workspace);
    await rm(root, { recursive: true, force: true });
  });
  const source = path.join(root, 'source'), target = path.join(root, 'generated');
  if (clone) await cp(frozen, source, { recursive: true });
  return { source, target };
}

async function prepare(source: string, target: string, preview = true) {
  const env: NodeJS.ProcessEnv = { ...process.env, BRIGHT_REVIT_BUNDLE: source, BRIGHT_REVIT_OUTPUT: target };
  if (preview) env.BRIGHT_BUILD_PREVIEW_EXPORTER = '1';
  else delete env.BRIGHT_BUILD_PREVIEW_EXPORTER;
  return run(process.execPath, [script], { cwd: project, env, windowsHide: true });
}

test('unified Revit packaging requires an explicit preview opt-in before any file access', async t => {
  const f = await fixture(t, false);
  await assert.rejects(prepare(f.source, f.target, false), /BRIGHT_BUILD_PREVIEW_EXPORTER=1.*preview builds/);
  await assert.rejects(stat(f.target), { code: 'ENOENT' });
});

integrationTest('preview copies all 14 frozen 1.7.0 variants and every verified helper', async t => {
  const f = await fixture(t, false);
  const result = await prepare(frozen, f.target);
  assert.match(result.stdout, /preview Revit component 1\.7\.0: 14 variants, 65 files/);
  const manifest = JSON.parse(await readFile(path.join(f.target, 'bundle.json'), 'utf8'));
  assert.equal(manifest.packageVersion, '1.7.0');
  assert.equal(manifest.variants.length, 14);
  for (const helper of helperNames) assert.equal(digest(await readFile(path.join(f.target, helper))), digest(await readFile(path.join(frozen, helper))), helper);
  for (const variant of manifest.variants) {
    for (const file of variant.files) assert.equal(digest(await readFile(path.join(f.target, 'payload', variant.id, file.name))), file.sha256);
  }
  assert.deepEqual((await readdir(f.target)).sort(), [...helperNames, 'payload'].sort());
  await prepare(frozen, f.target);
  assert.equal(digest(await readFile(path.join(f.target, 'bundle.json'))), digest(await readFile(path.join(frozen, 'bundle.json'))));
});

integrationTest('each frozen helper, including company license/NOTICE and the manifest, is pinned', async t => {
  for (const name of helperNames) {
    await t.test(name, async nested => {
      const f = await fixture(nested);
      const file = path.join(f.source, name);
      await writeFile(file, Buffer.concat([await readFile(file), Buffer.from('\n')]));
      await assert.rejects(prepare(f.source, f.target), /Revit helper verification failed/);
      await assert.rejects(stat(f.target), { code: 'ENOENT' });
    });
  }
});

integrationTest('missing BridgeMigration and corrupted exporter payload fail before generating output', async t => {
  const missing = await fixture(t);
  await unlink(path.join(missing.source, 'BridgeMigration.ps1'));
  await assert.rejects(prepare(missing.source, missing.target), /ENOENT/);
  await assert.rejects(stat(missing.target), { code: 'ENOENT' });
  const corrupt = await fixture(t);
  await writeFile(path.join(corrupt.source, 'payload/2020-net45/Bright.Vision.Revit.dll'), 'INERT CORRUPTED FIXTURE');
  await assert.rejects(prepare(corrupt.source, corrupt.target), /Revit payload verification failed/);
  await assert.rejects(stat(corrupt.target), { code: 'ENOENT' });
});

integrationTest('unexpected source and generated files are preserved and block preparation', async t => {
  const extra = await fixture(t);
  await writeFile(path.join(extra.source, 'Unreviewed.dll'), 'INERT EXTRA FIXTURE');
  await assert.rejects(prepare(extra.source, extra.target), /Unexpected source module files/);
  await assert.rejects(stat(extra.target), { code: 'ENOENT' });
  const f = await fixture(t, false);
  await mkdir(f.target);
  const note = path.join(f.target, 'user-note.txt');
  await writeFile(note, 'PRESERVE');
  await assert.rejects(prepare(frozen, f.target), /Generated module has unknown files/);
  assert.equal(await readFile(note, 'utf8'), 'PRESERVE');
});

integrationTest('old or incomplete bundles cannot enter the 1.7.0 preview', async t => {
  for (const mutate of [
    (value: { packageVersion: string; variants: unknown[] }) => { value.packageVersion = '1.4.0'; },
    (value: { packageVersion: string; variants: unknown[] }) => { value.variants.pop(); },
  ]) {
    const f = await fixture(t);
    const file = path.join(f.source, 'bundle.json');
    const manifest = JSON.parse(await readFile(file, 'utf8'));
    mutate(manifest);
    await writeFile(file, JSON.stringify(manifest));
    await assert.rejects(prepare(f.source, f.target), /Unexpected Revit component identity or variant count/);
    await assert.rejects(stat(f.target), { code: 'ENOENT' });
  }
});

integrationTest('source-target overlap and source links cannot redirect the packaging step', async t => {
  const f = await fixture(t);
  await assert.rejects(prepare(f.source, f.source), /directories must be separate/);
  await assert.rejects(prepare(f.source, path.join(f.source, 'nested-output')), /directories must be separate/);
  const linked = await fixture(t);
  await symlink(frozen, path.join(linked.source, 'unreviewed-junction'), 'junction');
  await assert.rejects(prepare(linked.source, linked.target), /Unsupported bundle entry|Bundle directory must not be a link|Bundle paths cannot contain links/);
  await assert.rejects(stat(linked.target), { code: 'ENOENT' });
});

integrationTest('generated hard links or redirected parent directories never overwrite unrelated bytes', async t => {
  const f = await fixture(t, false); await mkdir(f.target);
  const protectedFile = path.join(path.dirname(f.target), 'preserve-user-data.txt');
  await writeFile(protectedFile, 'PRESERVE'); await link(protectedFile, path.join(f.target, 'README.md'));
  await assert.rejects(prepare(frozen, f.target), /hard-linked bundle entry/);
  assert.equal(await readFile(protectedFile, 'utf8'), 'PRESERVE');
  const redirected = await fixture(t, false), actual = path.join(path.dirname(redirected.target), 'actual');
  await mkdir(actual); const junction = path.join(path.dirname(redirected.target), 'redirected'); await symlink(actual, junction, 'junction');
  await assert.rejects(prepare(frozen, path.join(junction, 'nested')), /Bundle paths cannot contain links/);
  assert.deepEqual(await readdir(actual), []);
});
