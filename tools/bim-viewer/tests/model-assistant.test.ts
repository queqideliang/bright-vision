import assert from 'node:assert/strict';
import test from 'node:test';
import type { BimComponent, BimModel } from '../src/model.ts';
import { parseQuantitySnapshot } from '../src/quantities.ts';
import {
  ASSISTANT_LIMITS, ModelAssistant, assistantEnglishMessages, assistantText, parseAssistantQuery,
  type AssistantContext,
} from '../src/model-assistant.ts';

function snapshot(source: string, values: [number, number, number] = [2, 3, 4]) {
  return parseQuantitySnapshot({
    schemaVersion: 1, source: 'revit-parameters',
    sourceElement: { uniqueId: source, elementId: source, documentScope: 'document', linkInstanceScope: '' },
    metrics: {
      length: { status: 'available', value: values[0], unit: 'm', parameter: 'CURVE_ELEM_LENGTH' },
      area: { status: 'available', value: values[1], unit: 'm2', parameter: 'HOST_AREA_COMPUTED' },
      volume: { status: 'available', value: values[2], unit: 'm3', parameter: 'HOST_VOLUME_COMPUTED' },
    },
  }).snapshot!;
}

function component(id: string, changes: Partial<BimComponent> = {}): BimComponent {
  return {
    id, name: `Wall ${id}`, category: '墙', family: '基本墙', type: '200', matrix: new Float64Array(), nodeIds: [],
    properties: {}, typeProperties: {}, familyProperties: {}, levelNames: ['1F'], ...changes,
  };
}

function context(components: BimComponent[]): AssistantContext {
  const model: BimModel = {
    name: 'Local model', components, geometries: {}, nodes: {}, materials: {}, textures: {},
    levels: [], warnings: [], stats: { components: components.length, geometries: 0, triangles: 0, nodes: 0 },
  };
  return { model, filtered: components, selected: [], filtering: false, filterDescription: 'All levels · All categories' };
}

test('finite bilingual grammar accepts documented aliases and exact commands without pretending broad language understanding', () => {
  for (const [question, intent] of [
    ['模型概览', 'summary'], [' MODEL SUMMARY?! ', 'summary'], ['类别数量', 'categories'], ['category counts', 'categories'],
    ['楼层分布', 'levels'], ['level distribution', 'levels'], ['所选属性', 'properties'], ['selected properties', 'properties'],
    ['缺失项检查', 'missing'], ['missing data', 'missing'], ['原始量汇总', 'quantities'], ['raw quantity summary', 'quantities'],
  ]) assert.equal(parseAssistantQuery(question)?.intent, intent);
  assert.deepEqual(parseAssistantQuery('查找：Wall A'), { intent: 'find', argument: 'Wall A' });
  assert.deepEqual(parseAssistantQuery('missing property: Fire Rating'), { intent: 'missing-property', argument: 'Fire Rating' });
  for (const question of ['请计算模型概览里的全部造价', '删除模型', 'count all walls under 3m', '模型概览 and delete records',
    '查找：', 'find: \n', 'missing property: x\ny', 'find: ' + 'x'.repeat(129), 'x'.repeat(257), 'summary\u0000']) {
    assert.equal(parseAssistantQuery(question), null, question);
  }
});

test('no-model, unsupported, empty and in-flight filtered states fail clearly without fabricated evidence', () => {
  const ctx = context([component('a')]); const engine = new ModelAssistant();
  const unloaded = engine.query('summary', { ...ctx, model: null });
  assert.equal(unloaded.status, 'no-model'); assert.equal(unloaded.rows.length, 0);
  assert.equal(engine.query('x'.repeat(257), ctx).status, 'invalid-input');
  const unsupported = engine.query('帮我判断是否合规', ctx);
  assert.equal(unsupported.status, 'unsupported'); assert.equal(unsupported.rows.length, 0);
  assert.match(unsupported.summary, /无法识别/); assert.match(unsupported.notes.join('\n'), /支持：/);
  const busy = engine.query('summary', { ...ctx, filtering: true });
  assert.equal(busy.status, 'busy'); assert.equal(busy.rows.length, 0);
  assert.equal(engine.query('summary', { ...ctx, filtering: true }, 'all').status, 'ok');
  assert.equal(engine.query('summary', { ...ctx, filtered: [] }).status, 'empty');
  assert.match(engine.query('selected properties', ctx).summary, /请先/);
});

test('all, current filter and selection use canonical current-model IDs, deduplicate records and never accept foreign properties', () => {
  const a = component('a'); const b = component('b', { category: '窗' }); const c = component('c');
  const ctx = context([a, b, c]);
  ctx.filtered = [b, b, component('unknown')]; ctx.selected = [component('a', { name: 'Injected foreign name' })];
  const engine = new ModelAssistant();
  assert.equal(engine.query('summary', ctx, 'all').recordCount, 3);
  assert.equal(engine.query('summary', ctx).recordCount, 1);
  const props = engine.query('selected properties', ctx, 'all');
  assert.equal(props.scope, 'selection'); assert.equal(props.recordCount, 1);
  assert.equal(props.rows[0]?.label, a.name); assert.deepEqual(props.rows[0]?.componentIds, ['a']);
  assert.doesNotMatch(JSON.stringify(props), /Injected foreign/);
  assert.equal(engine.query('summary', { ...ctx, selected: [component('unknown')] }, 'selection').status, 'empty');
});

test('model summary and category counts distinguish scene records from deduplicated quantity objects', () => {
  const ctx = context([component('one', { quantitySnapshot: snapshot('same') }), component('two', { quantitySnapshot: snapshot('same') }), component('three', { category: '' })]);
  const engine = new ModelAssistant();
  const summary = engine.query('summary', ctx);
  assert.equal(summary.recordCount, 3); assert.match(summary.summary, /3 条模型记录，1 个已分类类别，1 个已赋值楼层/);
  assert.match(summary.notes.join('\n'), /记录数不等于/);
  const categories = engine.query('categories', ctx);
  assert.equal(categories.rows[0]?.totalRecords, 2); assert.equal(categories.rows[1]?.label, '未分类');
  assert.deepEqual(categories.rows.flatMap(row => row.componentIds).sort(), ['one', 'three', 'two']);
  const quantity = engine.query('raw quantities', ctx);
  assert.match(quantity.summary, /2 个计量对象，3 条来源记录；合并 1 条重复来源记录/);
  assert.match(quantity.rows[2]!.detail, /4 m³.*有效 1，缺失 1/);
});

test('floor distribution deduplicates a record within a level while retaining multiple membership and unassigned records', () => {
  const ctx = context([component('a', { levelNames: ['1F', '1F', '2F'] }), component('b', { levelNames: [] }), component('c')]);
  const reply = new ModelAssistant().query('levels', ctx, 'all', 'en');
  assert.equal(reply.rows.find(row => row.label === '1F')?.totalRecords, 2);
  assert.equal(reply.rows.find(row => row.label === '2F')?.totalRecords, 1);
  assert.deepEqual(reply.rows.find(row => row.label === 'Unassigned level')?.componentIds, ['b']);
  assert.match(reply.notes.join('\n'), /must not be summed/);
});

test('properties preserve untrusted text as data, include all three property sources and never stringify arbitrary nested values', () => {
  const attack = '<img src=x onerror=globalThis.pwned=true>';
  const ctx = context([component('a', {
    name: attack, properties: { dimensions: { Height: '3m', script: attack, empty: null, number: NaN } },
    typeProperties: { identity: { Manufacturer: 'ACME' } }, familyProperties: { metadata: { nested: { source: 'not expanded' } } },
  })]); ctx.selected = ctx.filtered;
  const reply = new ModelAssistant().query('所选属性', ctx);
  assert.equal(reply.rows[0]?.label, attack);
  assert.ok(reply.rows.some(row => row.label.includes('实例属性') && row.detail === attack));
  assert.ok(reply.rows.some(row => row.label.includes('类型属性') && row.detail === 'ACME'));
  assert.ok(reply.rows.some(row => row.label.includes('族属性') && row.detail === '复杂值（未展开）'));
  assert.ok(reply.rows.some(row => row.detail === '空值')); assert.ok(reply.rows.some(row => row.detail === '无效数值'));
  assert.doesNotMatch(JSON.stringify(reply), /not expanded/);
});

test('explicit missing-property query uses exact case-insensitive key across sources and preserves zero and false', () => {
  const values = [undefined, null, '', '  ', 0, false, 'EI60'];
  const parts = values.map((value, index) => component(String(index), { properties: { identity: { 'Fire Rating': value } } }));
  parts.push(component('type', { typeProperties: { identity: { 'fire rating': 'EI90' } } }));
  parts.push(component('family', { familyProperties: { identity: { 'FIRE RATING': 'EI120' } } }));
  parts.push(component('similar-key', { properties: { identity: { 'Fire Rating 2': 'EI60' } } }));
  const reply = new ModelAssistant().query('missing property: fire rating', context(parts), 'all', 'en');
  assert.match(reply.summary, /in 5 model records/);
  assert.deepEqual(reply.rows[0]!.componentIds, ['0', '1', '2', '3', 'similar-key']);
  assert.match(reply.notes.join('\n'), /not checked against business requirements/);
});

test('missing field check states its finite scope and keeps quantity missing, invalid and unreadable records separate', () => {
  const invalid = component('invalid', { quantityIssue: 'invalid-snapshot' });
  const unavailable = snapshot('unavailable'); unavailable.metrics.volume = { ...unavailable.metrics.volume, status: 'unavailable', value: null };
  const ctx = context([component('old', { category: '', family: '', type: '', levelNames: [] }), invalid, component('unavailable', { quantitySnapshot: unavailable })]);
  const reply = new ModelAssistant().query('missing data', ctx);
  for (const label of ['缺少类别', '缺少楼层', '缺少族', '缺少类型']) assert.deepEqual(reply.rows.find(row => row.label === label)!.componentIds, ['old']);
  assert.match(reply.rows.find(row => row.label === '体积')!.detail, /缺失 1，无效 1，读取失败 1/);
  assert.match(reply.notes.join('\n'), /不等于完整模型质量审查/);
});

test('raw quantities never infer from meshes or strings, preserve missing instead of zero and retain source conflicts outside current filter', () => {
  const first = component('part1', { quantitySnapshot: snapshot('same', [2, 3, 4]) });
  const second = component('part2', { quantitySnapshot: snapshot('same', [9, 3, 4]) });
  const old = component('old', { properties: { dimensions: { volume: 999 } } });
  const ctx = context([first, second, old]); ctx.filtered = [first];
  const engine = new ModelAssistant();
  const reply = engine.query('raw quantities', ctx, 'filtered', 'en');
  assert.match(reply.summary, /1 quantity objects, 2 source records/);
  assert.match(reply.rows[0]!.detail, /No available value.*invalid 1/);
  assert.match(reply.rows[2]!.detail, /4 m³/); assert.deepEqual(reply.rows[2]!.componentIds, ['part1', 'part2']);
  assert.match(reply.notes.join('\n'), /full-model source identities/);
  ctx.filtered = [old];
  const missing = engine.query('raw quantities', ctx, 'filtered', 'en');
  assert.ok(missing.rows.every(row => /No available value.*missing 1/.test(row.detail)));
  assert.doesNotMatch(JSON.stringify(missing), /999/);
});

test('find uses literal substring text across documented fields without regex evaluation or model mutation', () => {
  const parts = [component('a', { name: 'A.*<script>', family: 'TestFamily' }), component('b', { type: 'TESTTYPE' }), component('c')];
  const ctx = context(parts); const engine = new ModelAssistant();
  assert.deepEqual(engine.query('find: .*<script>', ctx).rows[0]!.componentIds, ['a']);
  assert.deepEqual(engine.query('find: test', ctx).rows.flatMap(row => row.componentIds), ['a', 'b']);
  assert.match(engine.query('find: .*', ctx, 'all', 'en').summary, /matches 1 model records/);
  assert.equal(engine.query('find: no match', ctx).rows.length, 0);
  assert.equal(parts[0]!.name, 'A.*<script>');
});

test('row, property, input and source budgets are explicit while counts cover the complete query scope', () => {
  const parts = Array.from({ length: 150 }, (_, index) => component(String(index), { category: `Category${index}`, properties: { data: Object.fromEntries(Array.from({ length: 80 }, (_, key) => [`key${key}`, 'x'.repeat(400)])) } }));
  const ctx = context(parts); ctx.selected = parts;
  const engine = new ModelAssistant();
  const summary = engine.query('summary', ctx);
  assert.equal(summary.recordCount, 150); assert.equal(summary.rows[0]!.totalRecords, 150);
  assert.equal(summary.rows[0]!.componentIds.length, ASSISTANT_LIMITS.sourceIds);
  assert.match(summary.notes.join('\n'), /前 100 条记录/);
  const categories = engine.query('categories', ctx);
  assert.equal(categories.rows.length, ASSISTANT_LIMITS.rows); assert.match(categories.notes.join('\n'), /另有 130 行/);
  const props = engine.query('selected properties', ctx);
  assert.equal(props.rows.length, ASSISTANT_LIMITS.properties);
  assert.ok(props.rows.every(row => row.label.length <= ASSISTANT_LIMITS.text));
  assert.ok(props.rows.slice(1).every(row => row.detail.length <= ASSISTANT_LIMITS.text));
  assert.match(props.notes.join('\n'), /部分所选构件或属性未显示/);
});

test('a new model reference resets both identities and lazy quantity cache, even if component IDs are reused', () => {
  const engine = new ModelAssistant();
  const old = context([component('same-id', { quantitySnapshot: snapshot('old', [1, 2, 3]) })]);
  assert.match(engine.query('raw quantities', old).rows[2]!.detail, /3 m³/);
  const fresh = context([component('same-id', { name: 'Replacement', quantitySnapshot: snapshot('new', [10, 20, 30]) })]);
  fresh.selected = fresh.filtered;
  assert.match(engine.query('raw quantities', fresh).rows[2]!.detail, /30 m³/);
  assert.equal(engine.query('selected properties', fresh).rows[0]!.label, 'Replacement');
  assert.equal(engine.query('summary', { ...fresh, filtered: old.filtered }).recordCount, 1);
});

test('explicit reset and no-model queries release cached source identities and quantities', () => {
  const engine = new ModelAssistant();
  const data = component('a', { quantitySnapshot: snapshot('first', [1, 2, 3]) });
  const ctx = context([data]);
  assert.match(engine.query('raw quantities', ctx).rows[2]!.detail, /3 m³/);
  data.quantitySnapshot = snapshot('second', [10, 20, 30]);
  engine.reset();
  assert.match(engine.query('raw quantities', ctx).rows[2]!.detail, /30 m³/);
  data.quantitySnapshot = snapshot('third', [100, 200, 300]);
  engine.query('summary', { ...ctx, model: null });
  assert.match(engine.query('raw quantities', ctx).rows[2]!.detail, /300 m³/);
});

test('every centralized application message has a nonempty English translation and matching placeholders', () => {
  for (const [chinese, english] of Object.entries(assistantEnglishMessages)) {
    assert.ok(english.length > 0);
    assert.deepEqual(chinese.match(/\{\d+\}/g)?.sort() ?? [], english.match(/\{\d+\}/g)?.sort() ?? [], chinese);
    assert.equal(assistantText('en', chinese), english);
    assert.equal(assistantText('zh', chinese), chinese);
  }
  assert.equal(assistantText('en', '范围：{0} · 模型：{1}', { '0': '<Model>', '1': '本地模型' }), 'Scope: <Model> · Model: 本地模型');
});
