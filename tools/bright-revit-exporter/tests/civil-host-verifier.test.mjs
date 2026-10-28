import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { parseCsv, verifyCivilHost } from './verify-civil-host.mjs';

const definitions = {
  Length: { unit: 'm', source: 'CURVE_ELEM_LENGTH', power: 1, label: '长度' },
  Area: { unit: 'm2', source: 'HOST_AREA_COMPUTED', power: 2, label: '面积' },
  Volume: { unit: 'm3', source: 'HOST_VOLUME_COMPUTED', power: 3, label: '体积' },
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const guard = value => /^[\s\p{Cc}\ufeff]*[=+\-@]/u.test(String(value)) || /^[\t\r\n]/u.test(String(value)) ? `'${value}` : String(value);
const csvEncode = rows => '\ufeff' + rows.map(row => row.map(value => `"${guard(value ?? '').replaceAll('"', '""')}"`).join(',')).join('\r\n') + '\r\n';
const applies = (kind, metric) => metric === 'Volume' || (metric === 'Length' ? kind === 'wall' || kind === 'beam' : kind === 'wall' || kind === 'floor');
const saveJson = (file, value) => writeFile(file, JSON.stringify(value), 'utf8');

// These are synthetic verifier unit tests, never actual Revit acceptance evidence.
async function fixture(t) {
  const root = await mkdtemp(path.join(tmpdir(), 'bv-civil-verifier-synthetic-'));
  const canonical = await realpath(root);
  t.after(async () => {
    assert.equal(await realpath(root), canonical); assert.ok(path.basename(canonical).startsWith('bv-civil-verifier-synthetic-'));
    const parent = await realpath(tmpdir()); assert.equal(path.dirname(canonical).toLowerCase(), parent.toLowerCase());
    await rm(canonical, { recursive: true, force: true });
  });
  const runId = 'synthetic-test', run = path.join(root, `run-${runId}`), output = path.join(run, 'output');
  await mkdir(output, { recursive: true });
  const modelPath = path.join(root, 'synthetic.rvt'), pluginPath = path.join(root, 'synthetic.dll');
  const modelBytes = Buffer.from('Synthetic verifier model; not a Revit RVT'), pluginBytes = Buffer.from('Synthetic verifier plugin; not executable');
  await writeFile(modelPath, modelBytes); await writeFile(pluginPath, pluginBytes);
  const references = [];
  const records = ['wall', 'wall', 'beam', 'structural-column', 'floor', 'review'].map((kind, index) => {
    const record = { ElementId: String(index + 1), UniqueId: `uid-${index + 1}`, Name: index === 0 ? '=Wall,"A"\nline 2' : `Object ${index + 1}`,
      KindCode: kind, KindName: kind, Category: kind, Supported: kind !== 'review', ExclusionReason: kind === 'review' ? 'Unmapped family' : '',
      Issues: kind === 'review' ? ['Unmapped family'] : [], LevelId: 'L1', LevelName: 'Level 1', PhaseStatus: 'New', DesignOptionName: '主模型' };
    for (const [metric, definition] of Object.entries(definitions)) {
      const applicable = record.Supported && applies(kind, metric);
      const missing = kind === 'beam' && metric === 'Volume';
      const raw = kind === 'floor' && metric === 'Area' ? 0 : (index + 1) * 10 ** definition.power;
      record[metric] = { Status: !applicable ? 'not-applicable' : missing ? 'missing' : 'available', Value: applicable && !missing ? raw * 0.3048 ** definition.power : null,
        Unit: definition.unit, Source: applicable ? definition.source : '' };
      if (applicable) references.push({ elementId: record.ElementId, uniqueId: record.UniqueId, kind, metric,
        status: record[metric].Status, rawValue: missing ? null : raw, expectedSiValue: -999999, reported: { Value: -999999 } });
    }
    return record;
  });
  const snapshot = { Elements: records, Exclusions: [{ ElementId: '999', Name: 'Excluded', Reason: 'Outside civil scope' }], Notices: [],
    ModelTitle: 'Synthetic project', DocumentScope: 'synthetic-doc', CapturedAtUtc: '2026-10-28T00:01:00Z', ScopeLabel: '当前工程（根文档）',
    PhaseLabel: 'Phase 1（仅现有／新建）', DesignOptionLabel: '主模型', Revision: 0, CandidateCount: records.length + 1, LinkInstanceCount: 0, MissingLinkCount: 0 };
  const selected = [], seen = new Set(); for (const record of records) if (record.Supported && !seen.has(record.KindCode)) { seen.add(record.KindCode); selected.push(record); }
  const selection = { ...snapshot, Elements: selected, Exclusions: [], ScopeLabel: '当前选择（根文档）', CandidateCount: selected.length };
  const view = { ...snapshot, Elements: [records[0], records[4], records[5]], ScopeLabel: '当前视图（根文档）', CandidateCount: 4 };
  const supportedKinds = {}; for (const record of records) if (record.Supported) supportedKinds[record.KindCode] = (supportedKinds[record.KindCode] ?? 0) + 1;
  const report = { result: 'PASS', runId, processId: 123, revitYear: '2020', revitBuild: 'synthetic', sourceSaved: false,
    source: { path: modelPath, bytes: modelBytes.length, sha256Before: hash(modelBytes), sha256After: hash(modelBytes), hashUnchanged: true, isModifiedBefore: false, isModifiedAfter: true },
    plugin: { path: pluginPath, version: '1.5.0.0', sha256: hash(pluginBytes) }, projectRecordCount: records.length,
    nativeAvailableComparisons: references.filter(reference => reference.status === 'available').length, supportedKinds, selectionScopeCount: selected.length,
    viewScopeCount: view.Elements.length, nativeSelectionVerified: true, refreshAfterNativeChangeVerified: true, temporaryFixtureRolledBack: true,
    ribbonClickVerified: false, nativeWorkbenchWindowVerified: false, finishedUtc: '2026-10-28T00:02:00Z' };
  const config = { runId, outputDirectory: output, modelPath, exporterPath: pluginPath, expectedExporterSha256: hash(pluginBytes) };
  const launch = { runId, processId: 123, modelPath, modelSha256: hash(modelBytes), sourceSaved: false, startedAtUtc: '2026-10-28T00:00:00Z' };
  const buildManifestPath = path.join(root, 'synthetic-build.json');
  await saveJson(buildManifestPath, { packageVersion: '1.5.0', variants: [{ year: '2020', id: 'synthetic-2020', files: [{ name: 'Bright.Vision.Revit.dll', sha256: hash(pluginBytes) }] }] });
  await saveJson(path.join(root, 'prepared-model.json'), { copy: modelPath, bytes: modelBytes.length, sha256: hash(modelBytes) });
  await saveJson(path.join(run, 'config.json'), config); await saveJson(path.join(run, 'launch.json'), launch);
  await saveJson(path.join(output, 'civil-host-report.json'), report); await saveJson(path.join(output, 'project-snapshot.json'), snapshot);
  await saveJson(path.join(output, 'selection-snapshot.json'), selection); await saveJson(path.join(output, 'view-snapshot.json'), view); await saveJson(path.join(output, 'native-references.json'), references);
  await writeFile(path.join(output, 'project-summary.csv'), csvEncode(csvRows(snapshot, 'kind')), 'utf8');
  await writeFile(path.join(output, 'project-details.csv'), csvEncode(csvRows(snapshot, 'element')), 'utf8');
  return { root, output, report, snapshot, references, modelPath, pluginPath, options: { buildManifestPath } };
}

function csvRows(snapshot, mode) {
  const rows = [
    ['模型', snapshot.ModelTitle], ['来源文档标识', snapshot.DocumentScope], ['读取时间（UTC）', snapshot.CapturedAtUtc], ['读取范围', snapshot.ScopeLabel],
    ['阶段', snapshot.PhaseLabel], ['设计选项', snapshot.DesignOptionLabel], ['读取修订号', String(snapshot.Revision)],
    ['当前记录数', String(snapshot.Elements.length)], ['当前受支持记录数', String(snapshot.Elements.filter(record => record.Supported).length)],
    ['原始读取候选数', String(snapshot.CandidateCount)], ['原始读取排除记录数', String(snapshot.Exclusions.length)],
    ['链接实例数（未纳入本期量表）', '0'], ['未加载链接数', '0'], ['分组', mode === 'element' ? '按构件' : '按业务类别'], [],
  ];
  const heading = ['分组标识', '构件/分组', '业务类别', '原始楼层', '记录数', '受支持记录数', '未计量记录数', '来源ElementIds', '来源UniqueIds', '问题/未计量原因'];
  for (const definition of Object.values(definitions)) heading.push(`原生${definition.label}(${definition.unit})`, ...['有效数', '缺失数', '无效数', '读取失败数', '不适用数', '合计溢出', '来源与状态'].map(suffix => definition.label + suffix));
  rows.push(heading);
  const groups = new Map(); for (const record of snapshot.Elements) { const key = mode === 'element' ? record.ElementId : record.KindCode; if (!groups.has(key)) groups.set(key, []); groups.get(key).push(record); }
  for (const [key, records] of groups) {
    const supported = records.filter(record => record.Supported);
    const row = [key, mode === 'element' ? records[0].Name : records[0].KindName, records[0].KindName, 'Level 1', String(records.length), String(supported.length), String(records.length - supported.length),
      records.map(record => record.ElementId).join('\n'), records.map(record => record.UniqueId).join('\n'), records.flatMap(record => record.Issues).join('；')];
    for (const [metric] of Object.entries(definitions)) {
      const metrics = supported.map(record => record[metric]); const available = metrics.filter(value => value.Status === 'available');
      const na = supported.length === 0 || metrics.every(value => value.Status === 'not-applicable');
      row.push(available.length ? String(available.reduce((sum, value) => sum + value.Value, 0)) : na ? 'NA' : '',
        ...['available', 'missing', 'invalid', 'unavailable', 'not-applicable'].map(status => String(metrics.filter(value => value.Status === status).length)), '',
        records.map(record => `ElementId=${record.ElementId}; UniqueId=${record.UniqueId}; ${record.Supported
          ? `${record[metric].Status}; unit=${record[metric].Unit}; source=${record[metric].Source}` : `NA; ${record.ExclusionReason}`}`).join('\n'));
    }
    rows.push(row);
  }
  rows.push([], ['原始读取范围排除清单（不参与量表合计）'], ['ElementId', '名称', '原因'], ...snapshot.Exclusions.map(record => [record.ElementId, record.Name, record.Reason]));
  return rows;
}

test('synthetic civil verifier checks independent native values, NA, review exclusion and grouped/detail CSV', async t => {
  const value = await fixture(t); const result = await verifyCivilHost(value.output, value.options);
  assert.equal(result.result, 'PASS'); assert.equal(result.project.records, 6); assert.equal(result.project.supported, 5); assert.equal(result.project.review, 1);
  assert.equal(result.csv[0].rows, 5); assert.equal(result.csv[1].rows, 6); assert.equal(result.nativeReferences.availableComparisons, 10);
  assert.equal(result.ribbonClickVerified, false); assert.equal(result.nativeWorkbenchWindowVerified, false);
});

test('synthetic civil verifier rejects altered raw native values, even when declared expected values are unchanged', async t => {
  const value = await fixture(t); value.references[0].rawValue += 1; await saveJson(path.join(value.output, 'native-references.json'), value.references);
  await assert.rejects(verifyCivilHost(value.output, value.options), /raw native SI/u);
});

test('synthetic civil verifier rejects summary numbers and non-counting review values', async t => {
  for (const review of [false, true]) {
    const value = await fixture(t); const file = path.join(value.output, 'project-summary.csv'); const rows = parseCsv((await readFile(file, 'utf8')).replace(/^\ufeff/u, ''));
    const header = rows.findIndex(row => row[0] === '分组标识'), col = rows[header].indexOf('原生体积(m3)');
    const row = review ? rows.find(row => row[0] === 'review') : rows[header + 1]; row[col] = '999'; await writeFile(file, csvEncode(rows), 'utf8');
    await assert.rejects(verifyCivilHost(value.output, value.options), review ? /missing\/NA state/u : /independently expected/u);
  }
});

test('synthetic civil verifier rejects wrong CSV source identity and missing UTF8 BOM', async t => {
  const value = await fixture(t); const file = path.join(value.output, 'project-details.csv'); const rows = parseCsv((await readFile(file, 'utf8')).replace(/^\ufeff/u, ''));
  rows[rows.findIndex(row => row[0] === '分组标识') + 1][7] = 'unknown-source'; await writeFile(file, csvEncode(rows), 'utf8');
  await assert.rejects(verifyCivilHost(value.output, value.options), /source IDs/u);
  const noBom = await fixture(t); const other = path.join(noBom.output, 'project-summary.csv'); await writeFile(other, (await readFile(other, 'utf8')).replace(/^\ufeff/u, ''), 'utf8');
  await assert.rejects(verifyCivilHost(noBom.output, noBom.options), /UTF-8 BOM/u);
});

test('synthetic civil verifier rejects changed model/plugin identity and mismatched run ID', async t => {
  const model = await fixture(t); await writeFile(model.modelPath, 'changed'); await assert.rejects(verifyCivilHost(model.output, model.options), /RVT differs/u);
  const plugin = await fixture(t); await writeFile(plugin.pluginPath, 'changed'); await assert.rejects(verifyCivilHost(plugin.output, plugin.options));
  const run = await fixture(t); run.report.runId = 'another-run'; await saveJson(path.join(run.output, 'civil-host-report.json'), run.report); await assert.rejects(verifyCivilHost(run.output, run.options));
});

test('synthetic civil verifier rejects invalid applicability and unconfirmed temporary rollback', async t => {
  const na = await fixture(t); na.snapshot.Elements[3].Length = { Status: 'available', Value: 1, Unit: 'm', Source: 'CURVE_ELEM_LENGTH' };
  await saveJson(path.join(na.output, 'project-snapshot.json'), na.snapshot); await assert.rejects(verifyCivilHost(na.output, na.options), /must be NA/u);
  const rollback = await fixture(t); rollback.report.temporaryFixtureRolledBack = false; await saveJson(path.join(rollback.output, 'civil-host-report.json'), rollback.report);
  await assert.rejects(verifyCivilHost(rollback.output, rollback.options));
});
