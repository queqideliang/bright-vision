import assert from 'node:assert/strict';
import { open } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { inspectPackage } from '../src/import/packages.ts';
import { parseModelZip } from '../src/import/parse.ts';
const require = createRequire(import.meta.url);
const { extractArchiveEntry } = require('../electron/archive-extractor.cjs');
const directory = process.env.BIM_MODEL_TEST_DIR;
if (!directory) throw new Error('Set BIM_MODEL_TEST_DIR to the existing model sample directory.');
const signal = new AbortController().signal;
async function withSource(file, callback) {
  const handle = await open(file, 'r');
  try {
    const size = (await handle.stat()).size;
    return await callback({ size, slice: (start, end) => ({ arrayBuffer: async () => {
      const data = new Uint8Array(end - start); const result = await handle.read(data, 0, data.length, start);
      assert.equal(result.bytesRead, data.length); return data.buffer;
    } }) });
  } finally { await handle.close(); }
}
const input = path.join(directory, 'AB（不带设备）.zip');
const { choices } = await withSource(input, source => inspectPackage(source));
assert.equal(choices.length, 26);
const selected = choices[0];
const result = await extractArchiveEntry({ input, ...selected, root: path.resolve('test-results/archive-native'), helperDirectory: path.resolve('native/generated'), signal });
const stats = await withSource(result.path, async source => (await parseModelZip(source, result.name)).stats);
assert.equal(stats.components, 45174);
console.log(JSON.stringify({ nativeNestedZip: true, modelsInPackage: choices.length, stats }));
const rvtInput = path.join(directory, '..', '轻量化处理.zip');
const rvtChoices = await withSource(rvtInput, async source => (await inspectPackage(source)).choices);
assert.equal(rvtChoices.filter(choice => choice.kind === 'rvt').length, 7);
const rawRvt = rvtChoices.find(choice => choice.kind === 'rvt');
await assert.rejects(extractArchiveEntry({ input: rvtInput, ...rawRvt, root: path.resolve('test-results/archive-native'), helperDirectory: path.resolve('native/generated'), signal }), /RVT.*独立读取/u);
console.log(JSON.stringify({ rawRvtRejectedWithoutLaunchingAuthoringApp: true }));
