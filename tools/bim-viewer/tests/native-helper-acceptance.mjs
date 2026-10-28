import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { zipSync, strToU8 } from 'fflate';

// A genuine execution test of this project's fixed build-time helper only.
// No compiler is invoked here; no antivirus setting or blocked fixture changes.
const require = createRequire(import.meta.url);
const { extractArchiveEntry } = require('../electron/archive-extractor.cjs');
const helperDirectory = path.resolve('native/generated');
const helper = path.join(helperDirectory, 'ArchiveExtractor.exe');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const before = await readFile(helper), manifest = JSON.parse(await readFile(path.join(helperDirectory, 'manifest.json'), 'utf8'));
assert.equal(manifest.productVersion, '0.6.0'); assert.equal(manifest.executable.sha256, digest(before));
const directory = path.resolve('test-results', `native-helper-0.6.0-${randomUUID()}`), cache = path.join(directory, 'cache');
await mkdir(directory, { recursive: true });
const nested = zipSync({ 'fixture.json': strToU8('{"synthetic":true,"company":"Bright Vision"}') });
const input = path.join(directory, 'owned-test-package.zip');
await writeFile(input, zipSync({ 'nested/model.zip': nested, 'readme.txt': strToU8('inert source data') }));
const selected = { input, index: 0, kind: 'zip', name: 'model.zip', root: cache, helperDirectory };
const first = await extractArchiveEntry(selected); // Any protection failure stops this test; no alternate launch is attempted.
assert.deepEqual(await readFile(first.path), Buffer.from(nested));
assert.equal(first.size, nested.length); assert.ok(first.path.startsWith(cache + path.sep));
const ownedCount = (await readdir(cache)).length; assert.equal(ownedCount, 1);
await assert.rejects(extractArchiveEntry({ ...selected, index: 1 }), /无法读取包内模型/u);
assert.equal((await readdir(cache)).length, ownedCount, 'Failed non-ZIP selection must remove only its own request data.');
await assert.rejects(extractArchiveEntry({ ...selected, index: 100 }), /无法读取包内模型/u);
assert.equal((await readdir(cache)).length, ownedCount, 'Invalid entry must leave no partial output.');
await assert.rejects(extractArchiveEntry({ ...selected, kind: 'rvt' }), /RVT.*独立读取/u);
const aborted = new AbortController(); aborted.abort();
await assert.rejects(extractArchiveEntry({ ...selected, signal: aborted.signal }), /取消/u);
assert.equal((await readdir(cache)).length, ownedCount);
assert.equal(digest(await readFile(helper)), digest(before));
const report = { result: 'PASS', productVersion: '0.6.0', source: 'Synthetic ZIP created by this test; no user models',
  helper: { bytes: (await stat(helper)).size, sha256: digest(before) },
  checks: ['actual fixed helper executed for valid nested ZIP', 'extracted exact expected bytes', 'unsupported entry rejected with owned data cleanup', 'invalid index rejected with cleanup', 'raw RVT rejected before launching helper', 'pre-cancel rejected', 'fixed helper bytes unchanged'],
  helperWasCompiledByTest: false, blockedLegacyFixtureExecuted: false, securitySettingsChanged: false };
await writeFile(path.join(directory, 'report.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ ...report, directory }));
