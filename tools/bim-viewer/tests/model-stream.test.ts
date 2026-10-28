import assert from 'node:assert/strict';
import test from 'node:test';
import { strToU8, zipSync } from 'fflate';
import { crc32 } from 'node:zlib';
import { jsonRecords } from '../src/import/json-stream.ts';
import { ModelArchive, decodeZipName, type SliceSource } from '../src/import/zip.ts';
import { MODEL_LIMITS, parseModelZip } from '../src/import/parse.ts';

async function* oneByte(text: string) { for (const byte of strToU8(text)) yield new Uint8Array([byte]); }
const I = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
function fixture() {
  return zipSync(Object.fromEntries(Object.entries({
    'components/components-0.json': [{ Id: 'wall', name: '墙', Nodes: ['n'], matrix: I }],
    'nodes/nodes-0.json': { n: { geometry: 'g', material: 'm', matrix: I } },
    'geometries/geometries-0.json': { g: { type: 'MESH', positions: [0,0,0, 1,0,0, 0,1,0], indices: [0,1,2] } },
    'materials/materials-0.json': { m: { color: [1,0,0] } },
  }).map(([key, value]) => [key, strToU8(JSON.stringify(value))])));
}
test('legacy Chinese ZIP names and CP437 names are accepted while the UTF-8 flag remains strict', () => {
  const gbk = new Uint8Array([0xc4, 0xa3, 0xd0, 0xcd, 46, 122, 105, 112]);
  assert.equal(decodeZipName(gbk, 0), '模型.zip');
  assert.throws(() => decodeZipName(gbk, 0x800), /UTF-8/u);
  assert.equal(decodeZipName(new Uint8Array([99,97,102,0x82,46,122,105,112]), 0), 'café.zip');
});
test('the Unicode path extension is used only when it matches the original filename checksum', () => {
  const original = strToU8('model.zip'); const unicode = strToU8('模型.zip');
  const extra = new Uint8Array(9 + unicode.length); const view = new DataView(extra.buffer);
  view.setUint16(0, 0x7075, true); view.setUint16(2, 5 + unicode.length, true); extra[4] = 1;
  view.setUint32(5, crc32(original), true); extra.set(unicode, 9);
  assert.equal(decodeZipName(original, 0, extra), '模型.zip');
  extra[5] ^= 1;
  assert.equal(decodeZipName(original, 0, extra), 'model.zip');
});
test('incremental JSON preserves split UTF-8, escaped quotes, nested commas, scalars and prototype-looking keys', async () => {
  const rows = [];
  for await (const row of jsonRecords(oneByte('{"中文":{"nested":["a,b", "x\\\"y",null]},"__proto__":42}'), false, 'test')) rows.push(row);
  assert.deepEqual(rows, [['中文', { nested: ['a,b', 'x"y', null] }], ['__proto__', 42]]);
  const values = [];
  for await (const [, value] of jsonRecords(oneByte('[1, false, "字符串", {"a":[2,3]}]'), true, 'test')) values.push(value);
  assert.deepEqual(values, [1, false, '字符串', { a: [2,3] }]);
});
test('incremental JSON rejects trailing commas, missing delimiters and truncated UTF-8', async () => {
  for (const [text, array] of [['[1,]', true], ['{"a":1,}', false], ['[1', true], ['[1]x', true], ['[1,,2]', true], ['{"a":1 "b":2}', false]] as const) {
    await assert.rejects(async () => { for await (const _row of jsonRecords(oneByte(text), array, 'test')) { /* consume */ } }, /JSON/u);
  }
  async function* invalidUtf8() { yield new Uint8Array([91,34,0xe4]); }
  await assert.rejects(async () => { for await (const _row of jsonRecords(invalidUtf8(), true, 'test')) {} }, /JSON/u);
});
test('a file at the 2 GB boundary is read by bounded ranges, including offsets above the old 256 MiB limit', async () => {
  const bytes = fixture(); const size = 2_147_483_648; const padding = size - bytes.length;
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.length); const end = bytes.length - 22;
  const directory = v.getUint32(end + 16, true);
  for (let at = directory; at < end;) {
    v.setUint32(at + 42, v.getUint32(at + 42, true) + padding, true);
    at += 46 + v.getUint16(at + 28, true) + v.getUint16(at + 30, true) + v.getUint16(at + 32, true);
  }
  v.setUint32(end + 16, directory + padding, true);
  const reads: number[] = [];
  const file: SliceSource = { size, slice(start, end) { return { async arrayBuffer() {
    reads.push(end - start);
    assert(end - start <= 65_557, 'must not materialize the entire archive');
    const result = new Uint8Array(end - start);
    const from = Math.max(start, padding);
    if (end > from) result.set(bytes.subarray(from - padding, end - padding), from - start);
    return result.buffer;
  } }; } };
  const model = await parseModelZip(file, 'boundary.zip');
  assert.equal(model.components[0]!.id, 'wall'); assert.equal(model.stats.triangles, 1);
  assert(reads.reduce((a, b) => a + b, 0) < 100_000);
  await assert.rejects(parseModelZip({ ...file, size: size + 1 }, 'too-large.zip'), /2 GB/u);
});
test('File/Blob input never calls arrayBuffer on the complete archive', async () => {
  const blob = new Blob([fixture() as Uint8Array<ArrayBuffer>]);
  blob.arrayBuffer = async () => { throw new Error('whole archive allocation'); };
  assert.equal((await parseModelZip(blob, 'blob.zip')).stats.triangles, 1);
});
test('ZIP64 central-directory footer is supported and a broken locator is rejected', async () => {
  const original = fixture(); const end = original.length - 22;
  const bytes = new Uint8Array(original.length + 76); bytes.set(original.subarray(0, end)); bytes.set(original.subarray(end), end + 76);
  const v = new DataView(bytes.buffer); const old = new DataView(original.buffer, original.byteOffset, original.length);
  const count = old.getUint16(end + 10, true);
  v.setUint32(end, 0x06064b50, true); v.setBigUint64(end + 4, 44n, true);
  v.setBigUint64(end + 24, BigInt(count), true); v.setBigUint64(end + 32, BigInt(count), true);
  v.setBigUint64(end + 40, BigInt(old.getUint32(end + 12, true)), true); v.setBigUint64(end + 48, BigInt(old.getUint32(end + 16, true)), true);
  v.setUint32(end + 56, 0x07064b50, true); v.setBigUint64(end + 64, BigInt(end), true); v.setUint32(end + 72, 1, true);
  v.setUint16(end + 76 + 8, 0xffff, true); v.setUint16(end + 76 + 10, 0xffff, true);
  v.setUint32(end + 76 + 12, 0xffffffff, true); v.setUint32(end + 76 + 16, 0xffffffff, true);
  assert.equal((await parseModelZip(bytes, 'zip64.zip')).components.length, 1);
  bytes[end + 56] ^= 1;
  await assert.rejects(new ModelArchive(bytes, MODEL_LIMITS).inspect(), /ZIP64/u);
});
