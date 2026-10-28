import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, realpath, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Independent Node-only data verification. No production C# aggregation or
// conversion helper is executed, and expectedSiValue/reported are not oracles.
const DEFINITIONS = {
  Length: { unit: 'm', source: 'CURVE_ELEM_LENGTH', power: 1, label: '长度' },
  Area: { unit: 'm2', source: 'HOST_AREA_COMPUTED', power: 2, label: '面积' },
  Volume: { unit: 'm3', source: 'HOST_VOLUME_COMPUTED', power: 3, label: '体积' },
};
const KINDS = new Set(['wall', 'beam', 'structural-column', 'architectural-column', 'floor']);
const STATES = new Set(['available', 'missing', 'invalid', 'unavailable', 'not-applicable']);
const HASH = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (left, right) => process.platform === 'win32'
  ? path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase() : path.resolve(left) === path.resolve(right);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const key = row => JSON.stringify([row.ElementId, row.UniqueId]);
const referenceKey = row => JSON.stringify([row.elementId, row.uniqueId, row.metric]);

async function readBounded(file, limit = 64 * 1024 * 1024) {
  const info = await stat(file); assert.ok(info.isFile() && info.size <= limit, `Not a bounded regular file: ${file}`);
  const bytes = await readFile(file);
  return { bytes, identity: { path: await realpath(file), bytes: bytes.length, sha256: HASH(bytes) } };
}
async function jsonFile(file, limit) {
  const value = await readBounded(file, limit);
  return { data: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(value.bytes).replace(/^\ufeff/u, '')), identity: value.identity };
}
async function fileIdentity(file) {
  assert.ok(typeof file === 'string' && path.isAbsolute(file), 'Artifact paths must be absolute');
  const info = await stat(file); assert.ok(info.isFile(), `Not a regular file: ${file}`);
  const hash = createHash('sha256'); for await (const chunk of createReadStream(file)) hash.update(chunk);
  return { path: await realpath(file), bytes: info.size, sha256: hash.digest('hex') };
}
function near(actual, expected, where) {
  assert.ok(typeof actual === 'number' && Number.isFinite(actual) && Math.abs(actual - expected) <= Math.max(1e-10, Math.abs(expected) * 1e-10),
    `${where}: actual ${actual}, independently expected ${expected}`);
}
function applicable(kind, metric) {
  return metric === 'Volume' || (metric === 'Length' ? kind === 'wall' || kind === 'beam' : kind === 'wall' || kind === 'floor');
}
function protectedText(value) {
  const text = value === null || value === undefined ? '' : String(value);
  return (/^[\s\p{Cc}\ufeff]*[=+\-@]/u.test(text) || /^[\t\r\n]/u.test(text)) ? `'${text}` : text;
}

/** RFC4180 field/quote parser; quoted embedded newlines remain part of a cell. */
export function parseCsv(text) {
  const rows = []; let row = []; let field = ''; let state = 'start';
  for (let at = 0; at < text.length; at++) {
    const ch = text[at];
    if (state === 'quoted') {
      if (ch === '"') { if (text[at + 1] === '"') { field += '"'; at++; } else state = 'closed'; }
      else field += ch;
      continue;
    }
    if (ch === ',' || ch === '\r' || ch === '\n') {
      row.push(field); field = ''; state = 'start';
      if (ch !== ',') {
        assert.ok(ch === '\r' && text[at + 1] === '\n', 'CSV record separators must be CRLF'); at++;
        rows.push(row); row = [];
      }
    } else if (ch === '"') {
      assert.equal(state, 'start', 'Unexpected quote in an unquoted CSV field'); state = 'quoted';
    } else {
      assert.notEqual(state, 'closed', 'Unexpected characters after closing CSV quote'); field += ch; state = 'plain';
    }
  }
  assert.notEqual(state, 'quoted', 'Unterminated CSV quoted field');
  if (field.length || row.length || state !== 'start') { row.push(field); rows.push(row); }
  return rows;
}

async function csvFile(file) {
  const { bytes, identity } = await readBounded(file);
  assert.ok(bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf, 'CSV must have a UTF-8 BOM');
  const rows = parseCsv(new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\ufeff/u, ''));
  const start = rows.findIndex(row => row[0] === '分组标识'); assert.ok(start >= 0, 'CSV schedule header is missing');
  const endOffset = rows.slice(start + 1).findIndex(row => row.length === 1 && row[0] === '');
  const end = endOffset === -1 ? rows.length : start + 1 + endOffset;
  return { identity, rows, heading: rows[start], table: rows.slice(start + 1, end), metadata: rows.slice(0, start), tail: rows.slice(end) };
}

function validateSnapshot(snapshot, expectedScope) {
  assert.ok(object(snapshot) && Array.isArray(snapshot.Elements) && Array.isArray(snapshot.Exclusions) && Array.isArray(snapshot.Notices));
  assert.ok(typeof snapshot.DocumentScope === 'string' && snapshot.DocumentScope.length > 0);
  assert.ok(typeof snapshot.ModelTitle === 'string' && Number.isFinite(Date.parse(snapshot.CapturedAtUtc)));
  assert.equal(snapshot.ScopeLabel, expectedScope);
  assert.ok(Number.isSafeInteger(snapshot.CandidateCount) && snapshot.CandidateCount >= 0);
  assert.equal(snapshot.CandidateCount, snapshot.Elements.length + snapshot.Exclusions.length, 'Candidate coverage must account for records plus explicit exclusions');
  const ids = new Set(), uniqueIds = new Set();
  for (const record of snapshot.Elements) {
    assert.ok(typeof record.ElementId === 'string' && record.ElementId && typeof record.UniqueId === 'string' && record.UniqueId, 'Missing source identity');
    assert.ok(!ids.has(record.ElementId) && !uniqueIds.has(record.UniqueId), 'Duplicate native source record'); ids.add(record.ElementId); uniqueIds.add(record.UniqueId);
    assert.ok(Array.isArray(record.Issues) && typeof record.Supported === 'boolean');
    assert.ok(record.PhaseStatus === 'Existing' || record.PhaseStatus === 'New', 'Unsupported phase entered quantity records');
    if (record.Supported) { assert.ok(KINDS.has(record.KindCode)); assert.ok(!record.ExclusionReason, 'Excluded record declared supported'); }
    else { assert.equal(record.KindCode, 'review'); assert.ok(typeof record.ExclusionReason === 'string' && record.ExclusionReason.length > 0, 'Review record lost its reason'); }
    for (const [metric, definition] of Object.entries(DEFINITIONS)) {
      const value = record[metric]; assert.ok(object(value) && STATES.has(value.Status), 'Invalid metric state'); assert.equal(value.Unit, definition.unit);
      if (!record.Supported || !applicable(record.KindCode, metric)) {
        assert.equal(value.Status, 'not-applicable', 'Review/non-applicable values must be NA'); assert.equal(value.Value, null);
      } else {
        assert.notEqual(value.Status, 'not-applicable', 'Applicable metric was silently marked NA'); assert.equal(value.Source, definition.source);
        if (value.Status === 'available') assert.ok(typeof value.Value === 'number' && Number.isFinite(value.Value) && value.Value >= 0);
        else assert.equal(value.Value, null, 'Missing/error state fabricated a number');
      }
    }
  }
  const excludedIds = new Set();
  for (const excluded of snapshot.Exclusions) {
    assert.ok(typeof excluded.ElementId === 'string' && excluded.ElementId && typeof excluded.Reason === 'string' && excluded.Reason.length > 0);
    assert.ok(!ids.has(excluded.ElementId) && !excludedIds.has(excluded.ElementId), 'Excluded source also entered quantity records or repeated'); excludedIds.add(excluded.ElementId);
  }
  return new Map(snapshot.Elements.map(record => [key(record), record]));
}

function pairwiseSum(values) {
  let current = values.slice();
  while (current.length > 1) {
    const next = []; for (let index = 0; index < current.length; index += 2) next.push(current[index] + (current[index + 1] ?? 0)); current = next;
  }
  return current[0] ?? null;
}
function summarize(records, native) {
  const supported = records.filter(record => record.Supported);
  const metrics = {};
  for (const metric of Object.keys(DEFINITIONS)) {
    const summary = { value: null, available: 0, missing: 0, invalid: 0, unavailable: 0, 'not-applicable': 0, overflow: false };
    const values = [];
    for (const record of supported) {
      const value = native.get(JSON.stringify([record.ElementId, record.UniqueId, metric]));
      assert.ok(value, 'Native-checked metric missing from independent aggregation'); summary[value.status]++;
      if (value.status === 'available') values.push(value.value);
    }
    const sum = pairwiseSum(values); summary.overflow = sum !== null && !Number.isFinite(sum); summary.value = summary.overflow ? null : sum; metrics[metric] = summary;
  }
  return { count: records.length, supportedCount: supported.length, metrics };
}

function checkCsv(csv, snapshot, native, groupMode) {
  const metadata = new Map(csv.metadata.filter(row => row.length === 2 && row[0] !== '读取提示').map(row => [row[0], row[1]]));
  for (const [label, value] of [['模型', snapshot.ModelTitle], ['来源文档标识', snapshot.DocumentScope], ['读取时间（UTC）', snapshot.CapturedAtUtc],
    ['读取范围', snapshot.ScopeLabel], ['阶段', snapshot.PhaseLabel], ['设计选项', snapshot.DesignOptionLabel], ['读取修订号', String(snapshot.Revision)],
    ['当前记录数', String(snapshot.Elements.length)], ['当前受支持记录数', String(snapshot.Elements.filter(record => record.Supported).length)],
    ['原始读取候选数', String(snapshot.CandidateCount)], ['原始读取排除记录数', String(snapshot.Exclusions.length)],
    ['链接实例数（未纳入本期量表）', String(snapshot.LinkInstanceCount)], ['未加载链接数', String(snapshot.MissingLinkCount)]]) {
    assert.equal(metadata.get(label), protectedText(value), `CSV metadata mismatch: ${label}`);
  }
  assert.equal(metadata.get('分组'), groupMode === 'element' ? '按构件' : '按业务类别');
  const required = ['分组标识', '构件/分组', '业务类别', '原始楼层', '记录数', '受支持记录数', '未计量记录数', '来源ElementIds', '来源UniqueIds', '问题/未计量原因'];
  for (const definition of Object.values(DEFINITIONS)) required.push(`原生${definition.label}(${definition.unit})`, ...['有效数', '缺失数', '无效数', '读取失败数', '不适用数', '合计溢出', '来源与状态'].map(suffix => definition.label + suffix));
  assert.deepEqual(csv.heading, required, 'Unexpected CSV units or coverage columns');
  const groups = new Map();
  for (const record of snapshot.Elements) {
    const group = groupMode === 'element' ? key(record) : record.KindCode;
    if (!groups.has(group)) groups.set(group, []); groups.get(group).push(record);
  }
  assert.equal(csv.table.length, groups.size, 'CSV has the wrong number of scope groups');
  const used = new Set(), keys = new Set();
  for (const fields of csv.table) {
    assert.equal(fields.length, csv.heading.length); assert.ok(fields[0] && !keys.has(fields[0]), 'Duplicate/empty CSV row key'); keys.add(fields[0]);
    const ids = fields[7].split('\n');
    const group = [...groups].find(([name, records]) => !used.has(name) && protectedText(records.map(record => record.ElementId).join('\n')) === fields[7]);
    assert.ok(group, 'CSV group contains missing, additional, excluded or reordered source IDs');
    const [name, records] = group; used.add(name);
    assert.equal(fields[8], protectedText(records.map(record => record.UniqueId).join('\n')), 'CSV UniqueIds do not match exact native sources');
    assert.equal(ids.length, records.length);
    const expected = summarize(records, native);
    assert.equal(fields[4], String(expected.count)); assert.equal(fields[5], String(expected.supportedCount)); assert.equal(fields[6], String(expected.count - expected.supportedCount));
    if (groupMode === 'element') assert.equal(fields[1], protectedText(records[0].Name || `构件 ${records[0].ElementId}`));
    for (const record of records) {
      if (!record.Supported) assert.ok(fields[9].includes(record.ExclusionReason), 'CSV review row lost its non-counting reason');
      for (const issue of record.Issues) if (issue) assert.ok(fields[9].includes(issue), 'CSV lost a recorded source issue');
    }
    for (const [metric, definition] of Object.entries(DEFINITIONS)) {
      const start = csv.heading.indexOf(`原生${definition.label}(${definition.unit})`); const value = expected.metrics[metric];
      const na = expected.supportedCount === 0 || (value.available === 0 && value.missing === 0 && value.invalid === 0 && value.unavailable === 0 && value['not-applicable'] > 0);
      if (value.value !== null) { assert.match(fields[start], /^(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?$/u, 'CSV quantities must be invariant decimal numbers'); near(Number(fields[start]), value.value, `CSV ${name}/${metric}`); }
      else assert.equal(fields[start], na ? 'NA' : '', `CSV ${name}/${metric} missing/NA state`);
      for (const [offset, status] of [[1, 'available'], [2, 'missing'], [3, 'invalid'], [4, 'unavailable'], [5, 'not-applicable']]) assert.equal(fields[start + offset], String(value[status]), `CSV ${name}/${metric} ${status}`);
      assert.equal(fields[start + 6], value.overflow ? '是' : '');
      const provenance = fields[start + 7];
      for (const record of records) {
        assert.ok(provenance.includes(`ElementId=${record.ElementId}; UniqueId=${record.UniqueId}; `), 'CSV provenance lost source identity');
        assert.ok(provenance.includes(record.Supported ? `${record[metric].Status}; unit=${definition.unit}; source=${record[metric].Source}` : `NA; ${record.ExclusionReason}`), 'CSV provenance lost status/parameter or review exclusion');
      }
    }
  }
  assert.equal(used.size, groups.size);
  const exclusionTitle = csv.tail.findIndex(row => row[0] === '原始读取范围排除清单（不参与量表合计）');
  if (snapshot.Exclusions.length) {
    assert.ok(exclusionTitle >= 0); const excluded = csv.tail.slice(exclusionTitle + 2); assert.equal(excluded.length, snapshot.Exclusions.length);
    for (let index = 0; index < excluded.length; index++) {
      const expected = snapshot.Exclusions[index]; assert.deepEqual(excluded[index], [expected.ElementId, expected.Name, expected.Reason].map(protectedText));
    }
  } else assert.equal(exclusionTitle, -1);
  return { groupMode, rows: csv.table.length, sourceRecords: snapshot.Elements.length, supportedRecords: snapshot.Elements.filter(record => record.Supported).length };
}

async function validate(outputDirectory, options = {}) {
  const runDirectory = path.dirname(outputDirectory), preparedPath = path.join(path.dirname(runDirectory), 'prepared-model.json');
  const paths = { host: path.join(outputDirectory, 'civil-host-report.json'), project: path.join(outputDirectory, 'project-snapshot.json'), native: path.join(outputDirectory, 'native-references.json'),
    selection: path.join(outputDirectory, 'selection-snapshot.json'), view: path.join(outputDirectory, 'view-snapshot.json'), config: path.join(runDirectory, 'config.json'),
    launch: path.join(runDirectory, 'launch.json'), prepared: preparedPath };
  const inputs = {}; for (const [name, file] of Object.entries(paths)) inputs[name] = await jsonFile(file);
  const report = inputs.host.data, snapshot = inputs.project.data, references = inputs.native.data, config = inputs.config.data, launch = inputs.launch.data, prepared = inputs.prepared.data;
  assert.equal(report.result, 'PASS', 'Real host did not report a successful civil test');
  assert.equal(report.runId, config.runId); assert.equal(report.runId, launch.runId); assert.equal(path.basename(runDirectory), `run-${report.runId}`);
  assert.equal(report.processId, launch.processId, 'Host PID differs from the recorded launch');
  assert.ok(samePath(config.outputDirectory, outputDirectory)); assert.ok(samePath(config.modelPath, report.source.path)); assert.ok(samePath(launch.modelPath, report.source.path));
  assert.ok(samePath(prepared.copy, report.source.path));
  assert.equal(report.sourceSaved, false); assert.equal(launch.sourceSaved, false); assert.equal(report.source.hashUnchanged, true);
  assert.equal(report.refreshAfterNativeChangeVerified, true); assert.equal(report.temporaryFixtureRolledBack, true); assert.equal(report.nativeSelectionVerified, true);
  assert.equal(report.ribbonClickVerified, false); assert.equal(report.nativeWorkbenchWindowVerified, false);
  const source = await fileIdentity(report.source.path);
  for (const expected of [report.source.sha256Before, report.source.sha256After, prepared.sha256, launch.modelSha256]) assert.equal(source.sha256, String(expected).toLowerCase(), 'RVT differs from host or preparation/launch identity');
  assert.equal(source.bytes, prepared.bytes); assert.equal(source.bytes, report.source.bytes);
  const plugin = await fileIdentity(report.plugin.path); assert.ok(samePath(plugin.path, config.exporterPath)); assert.equal(report.plugin.version, '1.5.0.0');
  assert.equal(plugin.sha256, report.plugin.sha256.toLowerCase()); assert.equal(plugin.sha256, config.expectedExporterSha256.toLowerCase());
  const buildPath = options.buildManifestPath ?? fileURLToPath(new URL('../artifacts/BV-1.5.0-final/build-manifest.json', import.meta.url));
  const build = await jsonFile(buildPath); assert.equal(build.data.packageVersion, '1.5.0');
  const variants = build.data.variants.filter(variant => String(variant.year) === report.revitYear && variant.files.some(file => file.name === 'Bright.Vision.Revit.dll' && file.sha256.toLowerCase() === plugin.sha256));
  assert.ok(variants.length > 0, 'Installed plugin is not the frozen 1.5.0 payload for this Revit year');
  const records = validateSnapshot(snapshot, '当前工程（根文档）'); assert.equal(records.size, report.projectRecordCount); assert.ok(records.size > 0);
  const startedAt = Date.parse(launch.startedAtUtc), finishedAt = Date.parse(report.finishedUtc);
  assert.ok(Number.isFinite(startedAt) && Number.isFinite(finishedAt) && finishedAt >= startedAt, 'Missing or invalid launch/host completion time');
  for (const name of ['project', 'selection', 'view']) {
    const captured = Date.parse(inputs[name].data.CapturedAtUtc);
    assert.ok(captured >= startedAt - 1000 && captured <= finishedAt + 1000, `${name} snapshot timestamp lies outside this host run`);
  }
  assert.ok(Array.isArray(references)); const native = new Map(); let available = 0;
  for (const reference of references) {
    assert.ok(object(reference) && Object.hasOwn(DEFINITIONS, reference.metric), 'Unknown native reference metric');
    const record = records.get(JSON.stringify([reference.elementId, reference.uniqueId]));
    assert.ok(record?.Supported && reference.kind === record.KindCode, 'Native reference does not match an eligible source');
    assert.ok(applicable(record.KindCode, reference.metric), 'Native reference entered a non-applicable business metric');
    const id = referenceKey(reference); assert.ok(!native.has(id), 'Native reference repeated');
    assert.ok(['available', 'missing', 'invalid', 'unavailable'].includes(reference.status));
    const metric = record[reference.metric]; assert.equal(metric.Status, reference.status, 'Snapshot status differs from independent native parameter');
    let value = null;
    if (reference.status === 'available') {
      assert.ok(typeof reference.rawValue === 'number' && Number.isFinite(reference.rawValue) && reference.rawValue >= 0);
      value = reference.rawValue * 0.3048 ** DEFINITIONS[reference.metric].power;
      near(metric.Value, value, `${record.ElementId}/${reference.metric} raw native SI`); available++;
    } else { assert.equal(metric.Value, null); assert.equal(reference.rawValue, null); }
    native.set(id, { status: reference.status, value });
  }
  for (const record of snapshot.Elements) if (record.Supported) for (const metric of Object.keys(DEFINITIONS)) {
    const id = JSON.stringify([record.ElementId, record.UniqueId, metric]);
    if (applicable(record.KindCode, metric)) assert.ok(native.has(id), 'An applicable supported quantity lacks an independent native reference');
    else native.set(id, { status: 'not-applicable', value: null });
  }
  assert.ok(available > 0); assert.equal(available, report.nativeAvailableComparisons);
  const kinds = {}; for (const record of snapshot.Elements) if (record.Supported) kinds[record.KindCode] = (kinds[record.KindCode] ?? 0) + 1;
  assert.deepEqual(kinds, report.supportedKinds);
  const scopeChecks = {};
  for (const [name, label] of [['selection', '当前选择（根文档）'], ['view', '当前视图（根文档）']]) {
    const scoped = inputs[name].data; const scopedRecords = validateSnapshot(scoped, label);
    for (const property of ['ModelTitle', 'DocumentScope', 'PhaseLabel', 'DesignOptionLabel']) assert.equal(scoped[property], snapshot[property], `Scope ${name} changed ${property}`);
    for (const [id, record] of scopedRecords) {
      const original = records.get(id); assert.ok(original, `${name} scope returned a source outside the project snapshot`);
      assert.equal(record.KindCode, original.KindCode); assert.equal(record.Supported, original.Supported);
      for (const metric of Object.keys(DEFINITIONS)) { assert.equal(record[metric].Status, original[metric].Status); assert.equal(record[metric].Unit, original[metric].Unit); if (record[metric].Value !== null) near(record[metric].Value, original[metric].Value, `${name} ${record.ElementId}/${metric}`); }
    }
    assert.equal(scopedRecords.size, report[name === 'selection' ? 'selectionScopeCount' : 'viewScopeCount']);
    scopeChecks[name] = { records: scopedRecords.size, excluded: scoped.Exclusions.length, candidateCount: scoped.CandidateCount };
  }
  const chosen = new Map(); for (const record of snapshot.Elements) if (record.Supported && !chosen.has(record.KindCode)) chosen.set(record.KindCode, record);
  assert.deepEqual([...inputs.selection.data.Elements.map(key)].sort(), [...chosen.values()].map(key).sort(), 'Selection snapshot does not contain the exact first supported source of every chosen kind');
  const summaryCsv = await csvFile(path.join(outputDirectory, 'project-summary.csv'));
  const detailsCsv = await csvFile(path.join(outputDirectory, 'project-details.csv'));
  const csvChecks = [checkCsv(summaryCsv, snapshot, native, 'kind'), checkCsv(detailsCsv, snapshot, native, 'element')];
  return {
    result: 'PASS', runId: report.runId, validationKind: 'independent-node-civil-host-data-and-csv',
    hostReport: inputs.host.identity, source, plugin: { ...plugin, version: report.plugin.version, frozenVariants: variants.map(variant => variant.id) },
    host: { processId: report.processId, year: report.revitYear, build: report.revitBuild },
    artifacts: { ...Object.fromEntries(Object.entries(inputs).map(([name, value]) => [name, value.identity])), frozenBuild: build.identity, summaryCsv: summaryCsv.identity, detailsCsv: detailsCsv.identity },
    project: { records: snapshot.Elements.length, supported: snapshot.Elements.filter(record => record.Supported).length, review: snapshot.Elements.filter(record => !record.Supported).length,
      excluded: snapshot.Exclusions.length, candidateCount: snapshot.CandidateCount, documentScope: snapshot.DocumentScope, phase: snapshot.PhaseLabel, designOption: snapshot.DesignOptionLabel, supportedKinds: kinds },
    nativeReferences: { applicableComparisons: references.length, availableComparisons: available, oracle: 'rawValue independently multiplied by 0.3048^dimension; expectedSiValue and reported ignored' },
    summary: summarize(snapshot.Elements, native), scopes: scopeChecks, csv: csvChecks,
    refresh: { hostDeclaredNativeChangeInvalidation: true, hostDeclaredTemporaryFixtureRollback: true },
    sourceSaved: false, sourceFileHashUnchanged: true, isModifiedBefore: report.source.isModifiedBefore, isModifiedAfter: report.source.isModifiedAfter,
    ribbonClickVerified: false, nativeWorkbenchWindowVerified: false,
    limitations: ['This verifier reads host-written native references and data; it does not execute Revit or observe the workbench window.',
      'Refresh invalidation, native selection and temporary transaction rollback are host-driver assertions, bound to this run/report; saved RVT bytes are independently hash-checked.',
      'Host CSV grouping checks cover element and kind modes; original-level grouping remains covered by separate pure-domain tests.',
      'Source parameter quantities do not establish regional bill, quota, formwork or building-area calculation correctness.'],
  };
}

export async function verifyCivilHost(inputPath, options = {}) {
  const resolved = path.resolve(inputPath); const info = await stat(resolved);
  const outputDirectory = info.isDirectory() ? resolved : path.dirname(resolved);
  if (!info.isDirectory()) assert.equal(path.basename(resolved), 'civil-host-report.json');
  const outputPath = path.join(outputDirectory, 'civil-data-validation.json');
  try { const result = await validate(outputDirectory, options); await writeFile(outputPath, JSON.stringify(result, null, 2), 'utf8'); return result; }
  catch (error) { await writeFile(outputPath, JSON.stringify({ result: 'FAIL', input: resolved, error: String(error?.stack ?? error), ribbonClickVerified: false, nativeWorkbenchWindowVerified: false }, null, 2), 'utf8'); throw error; }
}

if (process.argv[1] && samePath(process.argv[1], fileURLToPath(import.meta.url))) {
  if (!process.argv[2]) { console.error('Usage: node tests/verify-civil-host.mjs <civil-host-report.json or output-directory>'); process.exitCode = 2; }
  else {
    try {
      const result = await verifyCivilHost(process.argv[2]);
      console.log(JSON.stringify({ result: result.result, runId: result.runId, project: result.project, nativeReferences: result.nativeReferences, csv: result.csv,
        sourceFileHashUnchanged: result.sourceFileHashUnchanged, ribbonClickVerified: false, nativeWorkbenchWindowVerified: false }));
    } catch (error) { console.error(String(error?.message ?? error)); process.exitCode = 1; }
  }
}
