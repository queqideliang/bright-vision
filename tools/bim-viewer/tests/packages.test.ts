import assert from 'node:assert/strict';
import test from 'node:test';
import { strToU8, zipSync } from 'fflate';
import { inspectPackage, unpackZipMember } from '../src/import/packages.ts';
import { parseModelZip } from '../src/import/parse.ts';
const I = [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
function files(prefix = '', id = 'wall') {
  return Object.fromEntries(Object.entries({
    'components/components-0.json': [{ Id: id, Nodes: ['node'] }],
    'nodes/nodes-0.json': { node: { geometry: 'mesh', material: 'mat', matrix: I } },
    'geometries/geometries-0.json': { mesh: { type: 'MESH', positions: [0,0,0,1,0,0,0,1,0], indices: [0,1,2] } },
  }).map(([name, value]) => [prefix + name, strToU8(JSON.stringify(value))]));
}
test('a direct model ZIP needs no additional selection', async () => {
  assert.equal((await inspectPackage(zipSync(files()))).choices.length, 0);
});
test('a package lists nested ZIP and RVT files by exact central-directory index', async () => {
  const { choices } = await inspectPackage(zipSync({ 'models/': new Uint8Array(), 'models/建筑.zip': zipSync(files()), '土建.rvt': strToU8('sample') }));
  assert.deepEqual(choices.map(c => [c.index, c.name, c.kind]), [[1, 'models/建筑.zip', 'zip'], [2, '土建.rvt', 'rvt']]);
});
test('only the selected nested ZIP is decompressed and its model can be parsed', async () => {
  const { archive, choices } = await inspectPackage(zipSync({ 'A.zip': zipSync(files('', 'A')), 'B.zip': zipSync(files('', 'B')) }));
  const selected = await unpackZipMember(archive, choices[1]!.index);
  try { assert.equal((await parseModelZip(selected.file, 'B.zip')).components[0]!.id, 'B'); }
  finally { await selected.cleanup(); }
});
test('multiple model folders can be selected without mixing their identical internal IDs', async () => {
  const bytes = zipSync({ ...files('A/', 'A'), ...files('B/', 'B') });
  const { choices } = await inspectPackage(bytes);
  assert.equal(choices.length, 2);
  const model = await parseModelZip(bytes, 'B', undefined, undefined, choices[1]!.prefix);
  assert.deepEqual(model.components.map(c => c.id), ['B']);
});
