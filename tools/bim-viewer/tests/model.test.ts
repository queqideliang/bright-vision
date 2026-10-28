import assert from 'node:assert/strict';
import test from 'node:test';
import { strToU8, zipSync } from 'fflate';
import { MODEL_LIMITS, inspectModelZip, parseModelZip } from '../src/import/parse.ts';

const I = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const triangle = { type: 'MESH', positions: [0, 0, 0, 100, 0, 0, 0, 100, 0], indices: [0, 1, 2], normals: [], uv: [0, 0, 1, 0, 0, 1] };
type Fixture = Record<string, unknown>;

function fixture(): Fixture {
  const componentMatrix = [...I]; componentMatrix[12] = 1000;
  const nodeMatrix = [...I]; nodeMatrix[12] = 200;
  return {
    'components/components-0-0.json': [{ Id: 'link_42', name: '测试墙', BuildInCategory: 'OST_Walls', type: '墙', familyName: '墙族', familySymbol: '200mm', matrix: componentMatrix, Nodes: [8], Property: 'instance', SymbolProperty: 'type', FamilyProperty: 'family' }],
    // Deliberately in different chunks: identifiers are global, not chunk-local.
    'nodes/nodes-1-0.json': { '8': { geometry: 19, material: 'mat', matrix: nodeMatrix } },
    'geometries/geometries-2-0.json': { '19': triangle },
    'materials/materials-0-0.json': { mat: { type: 'NormalMaterial', color: [0.8, 0.4, 0.1], opacity: 1, map: 'tile.png' } },
    'properties/properties-0-0.json': {
      instance: { 标识: { 标记: 'W-42', 启用: true, 空值: null, 数量: 2 }, Name: '实例' },
      type: { 尺寸: { 宽度: '200 mm' } },
      family: { 元数据: { 分类: '墙' } },
    },
    'LevelInfo.json': { '1F': { attribute: { id: 'level1', elevation: 0 }, elements: { 墙: [{ id: 'link_42', name: '测试墙' }] } } },
  };
}

function archive(files: Fixture, prefix = ''): Uint8Array {
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, value]) => [prefix + name, value instanceof Uint8Array ? value : strToU8(JSON.stringify(value))])));
}

test('global chunk references, matrices, property groups and exact linked IDs are preserved', async () => {
  const data = fixture();
  data['texture/tile.png'] = new Uint8Array([137, 80, 78, 71]);
  const progress: number[] = [];
  const model = await parseModelZip(archive(data), 'fixture.zip', p => progress.push(p.progress));
  assert.deepEqual(model.stats, { components: 1, nodes: 1, geometries: 1, triangles: 1 });
  assert.equal(model.components[0].matrix[12], 1000);
  assert.equal(model.components[0].category, '墙');
  assert.equal(model.components[0].categoryCode, 'OST_Walls');
  assert.equal(model.components[0].type, '200mm');
  assert.equal(model.nodes['8'].matrix[12], 200);
  assert.deepEqual(model.components[0].nodeIds, ['8']);
  assert.deepEqual(model.components[0].levelNames, ['1F']);
  assert.deepEqual(model.levels[0].componentIds, ['link_42']);
  assert.equal(model.components[0].properties['标识']['空值'], null);
  assert.equal(model.components[0].properties['标识']['启用'], true);
  assert.equal(model.components[0].properties['未分组字段'].Name, '实例');
  assert.equal(model.components[0].typeProperties['尺寸']['宽度'], '200 mm');
  assert.equal(model.components[0].familyProperties['元数据']['分类'], '墙');
  assert.deepEqual([...model.geometries['19'].indices], [0, 1, 2]);
  assert.equal(model.geometries['19'].uv?.length, 6);
  assert.equal(model.textures['tile.png'].mimeType, 'image/png');
  assert.equal(model.warnings.length, 0);
  assert.equal(progress.at(-1), 1);
  assert(progress.every((p, i) => p >= 0 && p <= 1 && (i === 0 || p >= progress[i - 1])));
});

test('one enclosing model directory is accepted', async () => {
  const model = await parseModelZip(archive(fixture(), '导出模型/'), 'prefixed.zip');
  assert.equal(model.components.length, 1);
});

test('Windows backslash paths are normalized and checked before parsing', async () => {
  const data = Object.fromEntries(Object.entries(fixture()).map(([name, value]) => [name.replaceAll('/', '\\'), value]));
  const model = await parseModelZip(archive(data), 'windows.zip');
  assert.equal(model.components.length, 1);
  const alias = fixture(); alias['nodes\\nodes-1-0.json'] = {};
  await assert.rejects(parseModelZip(archive(alias), 'alias.zip'), /重复文件名/u);
});

test('outer ZIP bundles and multiple model directories give actionable errors', async () => {
  await assert.rejects(parseModelZip(archive({ 'building.rvt.zip': archive(fixture()) }), 'outer.zip'), /外层.*内部/u);
  const data = { ...Object.fromEntries(Object.entries(fixture()).map(([k, v]) => [`a/${k}`, v])), ...Object.fromEntries(Object.entries(fixture()).map(([k, v]) => [`b/${k}`, v])) };
  await assert.rejects(parseModelZip(archive(data), 'two.zip'), /多个模型目录/u);
});

test('unsupported files, truncated ZIP and invalid JSON fail without a partial model', async () => {
  await assert.rejects(parseModelZip(strToU8('not a zip'), 'bad.zip'), /ZIP/u);
  await assert.rejects(parseModelZip(archive(fixture()).subarray(0, 150), 'bad.zip'), /ZIP/u);
  await assert.rejects(parseModelZip(archive({ 'readme.txt': 'hello' }), 'bad.zip'), /未找到构件/u);
  const data = fixture(); data['nodes/nodes-1-0.json'] = strToU8('{broken');
  await assert.rejects(parseModelZip(archive(data), 'bad.zip'), /有效的 UTF-8 JSON/u);
});

test('ZIP path traversal, absolute names and duplicate case aliases are rejected before extraction', async () => {
  for (const name of ['../escape.json', '/absolute.json', 'C:/file.json', 'a\\..\\escape.json', '\\absolute.json', 'x/__proto__/y.json']) {
    const data = fixture(); data[name] = {};
    await assert.rejects(parseModelZip(archive(data), 'unsafe.zip'), /不安全/u);
  }
  const data = fixture(); data['nodes/Nodes-1-0.json'] = {};
  await assert.rejects(parseModelZip(archive(data), 'duplicate.zip'), /重复文件名/u);
});

test('archive limits are enforced using the directory before decompression', async () => {
  const data = archive(fixture());
  assert.throws(() => inspectModelZip(data, { ...MODEL_LIMITS, compressedBytes: data.length - 1 }), /压缩文件大小/u);
  assert.throws(() => inspectModelZip(data, { ...MODEL_LIMITS, entries: 1 }), /文件数量/u);
  assert.throws(() => inspectModelZip(data, { ...MODEL_LIMITS, entryBytes: 20 }), /单个文件/u);
  assert.throws(() => inspectModelZip(data, { ...MODEL_LIMITS, uncompressedBytes: 50 }), /总大小/u);
  await assert.rejects(parseModelZip(data, 'limited.zip', undefined, { ...MODEL_LIMITS, components: 0 }), /构件数量/u);
});

test('encrypted archives, unsupported compression and mismatched local headers are rejected', () => {
  for (const mutation of ['encrypted', 'method', 'local-name']) {
    const data = archive(fixture());
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const end = data.length - 22;
    const central = view.getUint32(end + 16, true);
    if (mutation === 'encrypted') view.setUint16(central + 8, view.getUint16(central + 8, true) | 1, true);
    if (mutation === 'method') view.setUint16(central + 10, 99, true);
    if (mutation === 'local-name') data[30] ^= 1;
    assert.throws(() => inspectModelZip(data), /加密|压缩方法|文件名记录/u);
  }
});

test('a matching but incorrect ZIP checksum does not silently accept changed model data', async () => {
  const data = archive(fixture());
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const central = view.getUint32(data.length - 22 + 16, true);
  const local = view.getUint32(central + 42, true);
  const incorrect = view.getUint32(central + 16, true) ^ 1;
  view.setUint32(central + 16, incorrect, true);
  view.setUint32(local + 14, incorrect, true);
  await assert.rejects(parseModelZip(data, 'checksum.zip'), /校验失败/u);
});

test('invalid geometry indices, non-finite numbers and malformed transforms are rejected', async () => {
  for (const badGeometry of [
    { ...triangle, indices: [0, 1, 3] },
    { ...triangle, indices: [0, 1, 1.5] },
    { ...triangle, positions: [0, 0] },
    { ...triangle, uv: [0, 1] },
    { ...triangle, positions: [1e40, 0, 0, 100, 0, 0, 0, 100, 0] },
  ]) {
    const data = fixture(); data['geometries/geometries-2-0.json'] = { '19': badGeometry };
    await assert.rejects(parseModelZip(archive(data), 'bad.zip'), /几何|索引/u);
  }
  const data = fixture(); data['nodes/nodes-1-0.json'] = { '8': { geometry: 19, material: 'mat', matrix: [1] } };
  await assert.rejects(parseModelZip(archive(data), 'bad.zip'), /矩阵/u);
  const nonFinite = fixture(); nonFinite['geometries/geometries-2-0.json'] = strToU8('{"19":{"type":"MESH","positions":[1e999,0,0],"indices":[]}}');
  await assert.rejects(parseModelZip(archive(nonFinite), 'bad.zip'), /非有限/u);
});

test('missing references are explicit warnings; missing types are not invented', async () => {
  const data = fixture();
  data['properties/properties-0-0.json'] = { instance: { 组: { a: 'preserved' } } };
  data['LevelInfo.json'] = { '1F': { elements: { 墙: [{ id: '42' }] } } };
  const model = await parseModelZip(archive(data), 'missing.zip');
  assert(model.warnings.some(w => w.includes('类型属性')));
  assert(model.warnings.some(w => w.includes('族属性')));
  assert(model.warnings.some(w => w.includes('楼层')));
  assert.deepEqual(model.components[0].levelNames, []); // Never guess an ambiguous linked-model suffix.
  assert.equal(Object.keys(model.components[0].typeProperties).length, 0);
  assert.equal(model.components[0].properties['组'].a, 'preserved');
});

test('prototype-looking property names remain inert data', async () => {
  const data = fixture();
  data['properties/properties-0-0.json'] = strToU8('{"instance":{"__proto__":{"polluted":true},"constructor":{"prototype":{"polluted":true}}}}');
  const model = await parseModelZip(archive(data), 'properties.zip');
  const groups = model.components[0].properties;
  assert.equal(Object.getPrototypeOf(groups), null);
  assert.equal(Object.getPrototypeOf(groups['__proto__']), null);
  assert.equal(groups['__proto__'].polluted, true);
  assert.equal(({} as Record<string, unknown>).polluted, undefined);
});

test('duplicate geometry and component IDs cannot silently overwrite model identity', async () => {
  const geometryData = fixture(); geometryData['geometries/geometries-3-0.json'] = { '19': triangle };
  await assert.rejects(parseModelZip(archive(geometryData), 'duplicate.zip'), /几何标识重复/u);
  const componentData = fixture(); componentData['components/components-1-0.json'] = componentData['components/components-0-0.json'];
  await assert.rejects(parseModelZip(archive(componentData), 'duplicate.zip'), /构件标识重复/u);
});

test('identical shared properties repeated across chunks are accepted without changing values', async () => {
  const data = fixture();
  data['properties/properties-1-0.json'] = {
    instance: { Name: '实例', 标识: { 数量: 2, 空值: null, 启用: true, 标记: 'W-42' } },
    type: { 尺寸: { 宽度: '200 mm' } },
  };
  const model = await parseModelZip(archive(data), 'shared-properties.zip');
  assert.equal(model.components[0].properties['标识']['标记'], 'W-42');
  assert.equal(model.components[0].properties['标识']['启用'], true);
  assert.equal(model.components[0].typeProperties['尺寸']['宽度'], '200 mm');
});

test('conflicting shared property values still fail instead of being silently overwritten', async () => {
  const data = fixture();
  data['properties/properties-1-0.json'] = { instance: { 标识: { 标记: 'different' } } };
  await assert.rejects(parseModelZip(archive(data), 'conflicting.zip'), /属性标识 instance.*冲突/u);
});
