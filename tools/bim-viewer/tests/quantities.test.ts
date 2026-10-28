import assert from 'node:assert/strict';
import test from 'node:test';
import { strToU8, zipSync } from 'fflate';
import type { BimComponent } from '../src/model.ts';
import { parseModelZip } from '../src/import/parse.ts';
import {
  buildQuantityRows, createQuantityIndex, parseQuantitySnapshot, quantityCsv, summarizeQuantities,
  type QuantitySnapshot,
} from '../src/quantities.ts';

function snapshot(values: [number, number, number] = [3.048, 2, 0], source = 'element-1') {
  return {
    schemaVersion: 1, source: 'revit-parameters',
    sourceElement: { uniqueId: source, elementId: source, documentScope: 'doc', linkInstanceScope: 'root' },
    metrics: {
      length: { status: 'available', value: values[0], unit: 'm', parameter: 'CURVE_ELEM_LENGTH' },
      area: { status: 'available', value: values[1], unit: 'm2', parameter: 'HOST_AREA_COMPUTED' },
      volume: { status: 'available', value: values[2], unit: 'm3', parameter: 'HOST_VOLUME_COMPUTED' },
    },
  };
}

function component(id: string, raw?: unknown, fields: Partial<BimComponent> = {}): BimComponent {
  const quantity = parseQuantitySnapshot(raw);
  return {
    id, name: `Wall ${id}`, category: '墙', family: '基本墙', type: '200mm',
    matrix: new Float64Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]), nodeIds: [],
    properties: {}, typeProperties: {}, familyProperties: {}, levelNames: ['1F'],
    quantitySnapshot: quantity.snapshot, quantityIssue: quantity.issue, ...fields,
  };
}

test('snapshot validates explicit SI units, built-in parameters and zero quantities', () => {
  const parsed = parseQuantitySnapshot(snapshot());
  assert.equal(parsed.issue, undefined);
  assert.equal(parsed.snapshot?.metrics.length.value, 3.048);
  assert.equal(parsed.snapshot?.metrics.area.unit, 'm2');
  assert.equal(parsed.snapshot?.metrics.volume.value, 0);
  assert.equal(parsed.snapshot?.metrics.volume.status, 'available');
  const total = summarizeQuantities([component('wall', snapshot())]);
  assert.equal(total.metrics.volume.value, 0);
  assert.equal(total.metrics.volume.available, 1);
});

test('old components preserve absent quantities instead of deriving from properties or mesh', () => {
  const old = component('old', undefined, { properties: { 数量: { 体积: '123 m³', 长度: 100, volume: 123 } } });
  const row = summarizeQuantities([old]);
  assert.equal(row.count, 1);
  for (const metric of Object.values(row.metrics)) {
    assert.equal(metric.value, null); assert.equal(metric.available, 0); assert.equal(metric.missing, 1);
  }
  assert.deepEqual(parseQuantitySnapshot(undefined), {});
});

test('unsupported schemas, sources and invalid source identities fail closed', () => {
  const variants: unknown[] = [null, [], 'snapshot', { ...snapshot(), schemaVersion: 2 }, { ...snapshot(), source: 'mesh' },
    { ...snapshot(), metrics: [] }];
  for (const field of ['uniqueId', 'elementId', 'documentScope', 'linkInstanceScope']) {
    variants.push({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, [field]: 42 } });
    variants.push({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, [field]: 'x'.repeat(1025) } });
  }
  variants.push({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, documentScope: '' } });
  variants.push({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, linkInstanceScope: '   ' } });
  variants.push({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, uniqueId: '\u0000id' } });
  for (const raw of variants) {
    const parsed = parseQuantitySnapshot(raw);
    assert.equal(parsed.snapshot, undefined); assert.ok(parsed.issue);
    assert.equal(summarizeQuantities([component('bad', raw)]).metrics.volume.invalid, 1);
  }
  assert.ok(parseQuantitySnapshot({ ...snapshot(), sourceElement: { ...snapshot().sourceElement, linkInstanceScope: '' } }).snapshot);
});

test('invalid units and parameter names invalidate only the affected metric', () => {
  for (const area of [{ ...snapshot().metrics.area, unit: 'ft2' }, { ...snapshot().metrics.area, parameter: 'HOST_VOLUME_COMPUTED' }]) {
    const data = { ...snapshot(), metrics: { ...snapshot().metrics, area } };
    const parsed = parseQuantitySnapshot(data);
    assert.equal(parsed.issue, 'invalid-metric');
    const total = summarizeQuantities([component('bad-unit', data)]);
    assert.equal(total.metrics.area.value, null); assert.equal(total.metrics.area.invalid, 1);
    assert.equal(total.metrics.length.value, 3.048); assert.equal(total.metrics.volume.value, 0);
  }
});

test('non-finite, negative, textual and inconsistent status values never become totals', () => {
  const invalid = [NaN, Infinity, -Infinity, -1, '12', null, undefined];
  for (const value of invalid) {
    const raw = { ...snapshot(), metrics: { ...snapshot().metrics, length: { ...snapshot().metrics.length, value } } };
    assert.equal(summarizeQuantities([component('bad-number', raw)]).metrics.length.invalid, 1);
  }
  for (const [status, value] of [['missing', 0], ['unavailable', 5], ['invalid', 1], ['unknown', null], ['available', null]]) {
    const raw = { ...snapshot(), metrics: { ...snapshot().metrics, length: { ...snapshot().metrics.length, status, value } } };
    assert.equal(summarizeQuantities([component('bad-status', raw)]).metrics.length.invalid, 1);
  }
});

test('valid missing, invalid and API-unavailable states remain distinct from legitimate zeros', () => {
  const raw = snapshot();
  Object.assign(raw.metrics.length, { status: 'missing', value: null });
  Object.assign(raw.metrics.area, { status: 'invalid', value: null });
  Object.assign(raw.metrics.volume, { status: 'unavailable', value: null });
  const total = summarizeQuantities([component('states', raw)]);
  assert.equal(total.metrics.length.missing, 1); assert.equal(total.metrics.area.invalid, 1);
  assert.equal(total.metrics.volume.unavailable, 1);
  assert.ok(Object.values(total.metrics).every(metric => metric.value === null && metric.available === 0));
});

test('missing and invalid quantities are counted alongside independent length, area and volume totals', () => {
  const raw = snapshot([4, 10, 2], 'two'); Object.assign(raw.metrics.area, { status: 'missing', value: null });
  const totals = summarizeQuantities([component('one', snapshot([3, 20, 0], 'one')), component('two', raw), component('old')]);
  assert.equal(totals.count, 3);
  assert.equal(totals.metrics.length.value, 7); assert.equal(totals.metrics.area.value, 20); assert.equal(totals.metrics.volume.value, 2);
  assert.equal(totals.metrics.area.available, 1); assert.equal(totals.metrics.area.missing, 2);
});

test('one source with multiple scene records contributes quantity once and retains all selection IDs', () => {
  const components = [component('fragment-1', snapshot()), component('fragment-2', snapshot())];
  const rows = buildQuantityRows(components, 'component');
  assert.equal(rows.length, 1); assert.equal(rows[0].count, 1);
  assert.equal(rows[0].recordCount, 2); assert.equal(rows[0].duplicateSources, 1);
  assert.deepEqual(rows[0].componentIds, ['fragment-1', 'fragment-2']);
  assert.equal(rows[0].metrics.length.value, 3.048); assert.equal(rows[0].metrics.length.available, 1);
  assert.equal(summarizeQuantities(components).count, 1);
  assert.equal(buildQuantityRows([...components, components[0]], 'component')[0].recordCount, 2);
});

test('conflicting repeated source values or statuses invalidate that metric permanently', () => {
  const first = component('fragment-1', snapshot([3, 2, 1]));
  const second = component('fragment-2', snapshot([4, 2, 1]));
  const third = component('fragment-3', snapshot([3, 2, 1]));
  const total = summarizeQuantities([first, second, third]);
  assert.equal(total.metrics.length.invalid, 1); assert.equal(total.metrics.length.available, 0); assert.equal(total.metrics.length.value, null);
  assert.equal(total.metrics.area.value, 2); assert.equal(total.metrics.volume.value, 1);
  const missing = snapshot([3, 2, 1]); Object.assign(missing.metrics.area, { status: 'missing', value: null });
  assert.equal(summarizeQuantities([first, component('missing-fragment', missing)]).metrics.area.invalid, 1);
});

test('separate document or linked placement identities count separately even with the same Revit UniqueId', () => {
  const root = snapshot([1, 2, 3]); const link = snapshot([1, 2, 3]); const otherDoc = snapshot([1, 2, 3]);
  link.sourceElement.linkInstanceScope = 'link-1'; otherDoc.sourceElement.documentScope = 'doc-2';
  const total = summarizeQuantities([component('root', root), component('link', link), component('other', otherDoc)]);
  assert.equal(total.count, 3); assert.equal(total.metrics.volume.value, 9); assert.equal(total.duplicateSources, 0);
});

test('global source merging precedes category/level grouping and does not double-count multi-level objects', () => {
  const first = component('part-1', snapshot([3, 4, 5]), { levelNames: ['1F', '1F'] });
  const second = component('part-2', snapshot([3, 4, 5]), { levelNames: ['2F'] });
  const third = component('other', snapshot([1, 2, 3], 'other'), { levelNames: ['1F'] });
  const rows = buildQuantityRows([first, second, third], 'level');
  assert.equal(rows.length, 2);
  const multiple = rows.find(row => row.label === '多楼层（未拆分）')!;
  assert.equal(multiple.count, 1); assert.equal(multiple.recordCount, 2); assert.equal(multiple.metrics.volume.value, 5);
  assert.equal(rows.reduce((sum, row) => sum + row.metrics.volume.value!, 0), 8);
  assert.equal(buildQuantityRows([first], 'level')[0].label, '1F');
});

test('filtering or selecting one scene record cannot hide a full-model source conflict or level assignment', () => {
  const first = component('part-1', snapshot([3, 4, 5]), { levelNames: ['1F'], category: '墙' });
  const second = component('part-2', snapshot([4, 4, 5]), { levelNames: ['2F'], category: '梁' });
  const other = component('other', snapshot([10, 20, 30], 'other'), { levelNames: ['3F'] });
  const universe = [first, second, other];
  const total = summarizeQuantities([first], 'zh', universe);
  assert.equal(total.count, 1); assert.equal(total.recordCount, 2); assert.equal(total.duplicateSources, 1);
  assert.equal(total.metrics.length.value, null); assert.equal(total.metrics.length.invalid, 1);
  assert.equal(total.metrics.area.value, 4); assert.equal(total.metrics.volume.value, 5);
  assert.deepEqual(total.componentIds, ['part-1', 'part-2']);
  const levelRows = buildQuantityRows([first], 'level', 'zh', universe);
  assert.equal(levelRows.length, 1); assert.equal(levelRows[0].label, '多楼层（未拆分）');
  assert.equal(levelRows[0].metrics.length.invalid, 1);
  const categoryRows = buildQuantityRows([first], 'category', 'en', universe);
  assert.equal(categoryRows[0].label, 'Conflicting categories');
  assert.equal(summarizeQuantities([other], 'zh', universe).metrics.length.value, 10);
  assert.equal(summarizeQuantities([], 'zh', universe).count, 0);
  assert.equal(summarizeQuantities([component('not-in-model', snapshot())], 'zh', universe).count, 0);
});

test('scoped legacy or malformed snapshots remain independent scene records in the full model', () => {
  const old = component('old'); const bad = component('bad', null); const valid = component('valid', snapshot());
  const universe = [old, bad, valid];
  const oldTotal = summarizeQuantities([old], 'zh', universe);
  assert.equal(oldTotal.count, 1); assert.equal(oldTotal.metrics.length.missing, 1);
  const badTotal = summarizeQuantities([bad], 'zh', universe);
  assert.equal(badTotal.count, 1); assert.equal(badTotal.metrics.length.invalid, 1);
  const validTotal = summarizeQuantities([valid], 'zh', universe);
  assert.equal(validTotal.count, 1); assert.equal(validTotal.metrics.length.available, 1);
});

test('reusable index matches pure APIs for source conflicts, duplicate fragments, links, empty and unknown scopes', () => {
  const first = component('part-1', snapshot([3, 4, 5]), { levelNames: ['1F'], category: '墙' });
  const second = component('part-2', snapshot([4, 4, 5]), { levelNames: ['2F'], category: '梁' });
  const linked = snapshot([7, 8, 9]); linked.sourceElement.linkInstanceScope = 'another-link';
  const old = component('old'); const bad = component('bad', null);
  const other = component('other', linked, { category: '板', levelNames: ['3F'] });
  const universe = [first, second, other, old, bad];
  const index = createQuantityIndex(universe);
  for (const scope of [universe, [first], [second], [old], [bad], [other], [other, second, first, old], [first, first], [], [component('unknown')]]) {
    for (const language of ['zh', 'en'] as const) {
      for (const mode of ['component', 'category', 'level'] as const) {
        assert.deepEqual(index.buildRows(scope, mode, language), buildQuantityRows(scope, mode, language, universe));
      }
      assert.deepEqual(index.summarize(scope, language), summarizeQuantities(scope, language, universe));
    }
  }
  assert.equal(index.summarize([first]).metrics.length.invalid, 1);
  assert.equal(index.buildRows([first], 'level')[0].label, '多楼层（未拆分）');
  assert.equal(index.summarize([first, other]).metrics.volume.value, 14);
});

test('index results are detached and model identity/metric changes require an explicit new index', () => {
  const first = component('first', snapshot([3, 4, 5]));
  const universe = [first]; const index = createQuantityIndex(universe);
  const expected = index.buildRows(universe, 'component');
  const returned = index.buildRows(universe, 'component');
  returned[0].componentIds.push('injected');
  returned[0].sources[0].elementId = 'changed';
  returned[0].metrics.length.value = 999;
  assert.deepEqual(index.buildRows(universe, 'component'), expected);
  const summary = index.summarize(universe);
  summary.componentIds.length = 0; summary.sources[0].uniqueId = 'changed'; summary.metrics.volume.value = 999;
  assert.equal(index.summarize(universe).metrics.volume.value, 5);
  first.quantitySnapshot!.metrics.length.value = 88;
  first.quantitySnapshot!.sourceElement.uniqueId = 'new-identity';
  first.levelNames.push('2F');
  assert.deepEqual(index.buildRows(universe, 'component'), expected);
  const replacement = createQuantityIndex(universe);
  assert.equal(replacement.summarize(universe).metrics.length.value, 88);
  assert.equal(replacement.buildRows(universe, 'level')[0].label, '多楼层（未拆分）');
});

test('conflicting categories form a distinct group rather than attributing one source twice', () => {
  const rows = buildQuantityRows([component('part-1', snapshot()), component('part-2', snapshot(), { category: '梁' })], 'category', 'en');
  assert.equal(rows.length, 1); assert.equal(rows[0].label, 'Conflicting categories');
  assert.equal(rows[0].metrics.length.available, 1); assert.equal(rows[0].recordCount, 2);
});

test('category names resembling prototypes and grouping sentinels remain ordinary independent groups', () => {
  const rows = buildQuantityRows([
    component('one', snapshot([1, 2, 3], 'one'), { category: '__proto__' }),
    component('two', snapshot([1, 2, 3], 'two'), { category: 'constructor' }),
    component('three', snapshot([1, 2, 3], 'three'), { category: '', levelNames: [] }),
    component('four', snapshot([1, 2, 3], 'four'), { category: '未分类' }),
  ], 'category');
  assert.equal(rows.length, 4); assert.equal(new Set(rows.map(row => row.key)).size, 4);
  assert.equal(buildQuantityRows([component('old', undefined, { levelNames: [] })], 'level', 'en')[0].label, 'Unassigned level');
});

test('compensated totals retain small contributions and detect overflow instead of exporting Infinity', () => {
  const components = [1e16, 1, 1].map((value, index) => component(String(index), snapshot([value, 0, 0], String(index))));
  assert.equal(summarizeQuantities(components).metrics.length.value, 1e16 + 2);
  const tooLarge = [component('one', snapshot([Number.MAX_VALUE, 0, 0], 'one')), component('two', snapshot([Number.MAX_VALUE, 0, 0], 'two'))];
  const summary = summarizeQuantities(tooLarge).metrics.length;
  assert.equal(summary.available, 2); assert.equal(summary.value, null); assert.equal(summary.overflow, true);
});

test('empty scopes have no fabricated quantity totals', () => {
  assert.deepEqual(buildQuantityRows([], 'component'), []);
  const total = summarizeQuantities([]);
  assert.equal(total.count, 0); assert.equal(total.recordCount, 0);
  assert.ok(Object.values(total.metrics).every(metric => metric.value === null && metric.missing === 0));
});

test('CSV preserves precision, SI units, provenance and availability in both languages', () => {
  const rows = buildQuantityRows([component('one', snapshot([0.123456789012345, 0, 9]))], 'component');
  const english = quantityCsv(rows, { language: 'en', modelName: 'Model', scope: 'Current filter', mode: 'component' });
  assert.ok(english.startsWith('\ufeff')); assert.match(english, /Length \(m\)/u); assert.match(english, /Area \(m2\)/u);
  assert.match(english, /0\.123456789012345/u); assert.match(english, /Revit instance built-in parameters/u);
  assert.match(english, /Length available/u); assert.match(english, /element-1/u); assert.match(english, /documentScope/u);
  const chinese = quantityCsv(rows, { language: 'zh', modelName: '测试工程', scope: '当前选择' });
  assert.match(chinese, /体积 \(m3\)/u); assert.match(chinese, /当前选择/u); assert.match(chinese, /体积有效数/u);
  const absent = quantityCsv(buildQuantityRows([component('old')], 'component'), { language: 'zh', modelName: '', scope: '全部' });
  assert.doesNotMatch(absent, /undefined|NaN|Infinity/u);
});

test('CSV quoting handles commas, quotes, newlines and formula injection in every untrusted text column', () => {
  const data = component('=component', snapshot(), { name: '=SUM(1,2)', category: ' @command', levelNames: ['\tformula'] });
  const csv = quantityCsv(buildQuantityRows([data], 'component'), { language: 'en', modelName: '=MODEL()', scope: '\n=scope' });
  assert.match(csv, /"'=SUM\(1,2\)"/u); assert.match(csv, /"' @command"/u);
  assert.match(csv, /"'\tformula"/u); assert.match(csv, /"'=component"/u);
  assert.match(csv, /"'=MODEL\(\)"/u); assert.match(csv, /"'\n=scope"/u);
  for (const whitespace of ['\ufeff', '\u00a0', '\u2003']) {
    const guarded = quantityCsv([], { language: 'zh', modelName: `${whitespace}=MODEL()`, scope: '当前筛选' });
    assert.ok(guarded.includes(`"'${whitespace}=MODEL()"`));
  }
  const special = quantityCsv(buildQuantityRows([component('safe', snapshot(), { name: '墙,"A"\n第二行' })], 'component'), { language: 'zh', modelName: '', scope: '' });
  assert.match(special, /"墙,""A""\n第二行"/u);
});

function modelArchive(rawSnapshot?: unknown): Uint8Array {
  const files = {
    'components/components-0.json': [{ Id: 'wall', name: '墙', type: '墙', Nodes: ['n'], QuantitySnapshot: rawSnapshot }],
    'nodes/nodes-0.json': { n: { geometry: 'g', material: 'm' } },
    'geometries/geometries-0.json': { g: { type: 'MESH', positions: [0, 0, 0, 1, 0, 0, 0, 1, 0], indices: [0, 1, 2] } },
    'materials/materials-0.json': { m: { color: [1, 0, 0] } },
    'LevelInfo.json': { '1F': { elements: { 墙: [{ id: 'wall' }] } } },
  };
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, value]) => [name, strToU8(JSON.stringify(value))])));
}

test('ZIP parser carries validated quantity snapshots into components without changing geometry', async () => {
  const model = await parseModelZip(modelArchive(snapshot()), 'quantity.zip');
  assert.equal(model.stats.triangles, 1); assert.equal(model.components.length, 1);
  assert.equal(model.components[0].quantitySnapshot?.metrics.length.value, 3.048);
  assert.equal(model.components[0].quantityIssue, undefined);
  assert.equal(buildQuantityRows(model.components, 'level')[0].label, '1F');
});

test('ZIP parser keeps old models and invalid/unsupported snapshots viewable while excluding false quantities', async () => {
  for (const raw of [undefined, null, { ...snapshot(), schemaVersion: 22 }, { ...snapshot(), metrics: { ...snapshot().metrics, volume: { status: 'available', value: 123, unit: 'ft3', parameter: 'HOST_VOLUME_COMPUTED' } } }]) {
    const model = await parseModelZip(modelArchive(raw), 'quantity.zip');
    assert.equal(model.stats.triangles, 1);
    const total = summarizeQuantities(model.components);
    assert.equal(total.metrics.volume.value, null);
    assert.equal(raw === undefined ? total.metrics.volume.missing : total.metrics.volume.invalid, 1);
  }
});

// Compile-time assurance that the decoder exposes only the supported normalized contract.
const _snapshotType: QuantitySnapshot | undefined = parseQuantitySnapshot(snapshot()).snapshot;
assert.equal(_snapshotType?.schemaVersion, 1);
