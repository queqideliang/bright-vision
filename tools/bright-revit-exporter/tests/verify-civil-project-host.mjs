import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, realpath, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseCsv } from './verify-civil-host.mjs';

// Node-only evidence verification. No Autodesk/production DLL is loaded, no
// production aggregation helper is executed, and neither expectedSiValue nor
// reported fields are the numerical oracle: rawValue is converted here.
const MODULE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const METRICS = {
  Length: { power: 1, unit: 'm', source: 'CURVE_ELEM_LENGTH', label: '长度' },
  Area: { power: 2, unit: 'm2', source: 'HOST_AREA_COMPUTED', label: '面积' },
  Volume: { power: 3, unit: 'm3', source: 'HOST_VOLUME_COMPUTED', label: '体积' },
};
const KINDS = { wall: 9, floor: 5, 'structural-column': 30, beam: 370 };
const PARAMETERS = {
  wall: ['WALL_BASE_CONSTRAINT'], floor: ['LEVEL_PARAM'],
  beam: ['INSTANCE_REFERENCE_LEVEL_PARAM', 'FAMILY_LEVEL_PARAM', 'SCHEDULE_LEVEL_PARAM'],
  'structural-column': ['FAMILY_BASE_LEVEL_PARAM', 'FAMILY_LEVEL_PARAM', 'SCHEDULE_LEVEL_PARAM'],
};
const DATA_FILES = ['native-project-snapshot.json', 'native-metric-references.json', 'native-level-references.json',
  'selection-native-snapshot.json', 'project-applied-snapshot.json', 'project-reloaded-snapshot.json',
  'quantity-project.json', 'project-quantity-level.csv', 'project-segment.csv', 'project-element.csv'];
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (a, b) => process.platform === 'win32'
  ? path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase() : path.resolve(a) === path.resolve(b);
const isHash = text => typeof text === 'string' && /^[a-f0-9]{64}$/iu.test(text);
const applicable = (kind, metric) => metric === 'Volume' || (metric === 'Length' ? kind === 'wall' || kind === 'beam' : kind === 'wall' || kind === 'floor');
function near(actual, expected, context) {
  assert.ok(typeof actual === 'number' && Number.isFinite(actual) && Number.isFinite(expected)
    && Math.abs(actual - expected) <= Math.max(1e-10, Math.abs(expected) * 1e-10), `${context}: ${actual} != independently expected ${expected}`);
}
function equalSet(actual, expected, context) {
  assert.equal(actual.length, new Set(actual).size, `${context}: duplicate identity`);
  assert.deepEqual([...actual].sort(), [...expected].sort(), context);
}
function indexUnique(records, field, context) {
  assert.ok(Array.isArray(records), `${context}: missing array`);
  const map = new Map();
  for (const row of records) {
    assert.ok(row && typeof row[field] === 'string' && row[field].length > 0 && !map.has(row[field]), `${context}: missing/duplicate ${field}`);
    map.set(row[field], row);
  }
  return map;
}
function child(root, name) {
  assert.ok(typeof name === 'string' && name.length > 0 && path.basename(name) === name && !/[\\/]/u.test(name) && name !== '.' && name !== '..', 'Artifact names must be plain filenames');
  return path.join(root, name);
}
async function identity(file, maxBytes = 128 * 1024 * 1024) {
  assert.ok(typeof file === 'string' && path.isAbsolute(file), `Expected an absolute file path: ${file}`);
  const info = await stat(file); assert.ok(info.isFile() && info.size <= maxBytes, `Not a bounded regular evidence file: ${file}`);
  const hash = createHash('sha256'); for await (const chunk of createReadStream(file)) hash.update(chunk);
  return { path: await realpath(file), bytes: info.size, sha256: hash.digest('hex') };
}
async function jsonFile(file) {
  const id = await identity(file, 64 * 1024 * 1024);
  const bytes = await readFile(file);
  return { data: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\ufeff/u, '')), identity: id };
}
async function hashMatches(file, expected, expectedBytes) {
  assert.ok(isHash(expected), `Invalid recorded SHA256: ${file}`);
  const id = await identity(file);
  assert.equal(id.sha256, expected.toLowerCase(), `Evidence SHA256 mismatch: ${file}`);
  if (expectedBytes !== undefined) assert.equal(id.bytes, expectedBytes, `Evidence length mismatch: ${file}`);
  return id;
}
function protectedText(value) {
  const text = value == null ? '' : String(value);
  return (/^[\s\p{Cc}\ufeff]*[=+\-@]/u.test(text) || /^[\t\r\n]/u.test(text)) ? `'${text}` : text;
}
function nativeFacts(snapshot) {
  const copy = structuredClone(snapshot);
  for (const key of ['ProjectId', 'ProjectName', 'ProjectRevision', 'ProjectNotices']) delete copy[key];
  for (const record of copy.Elements) for (const key of ['QuantityLevelId', 'QuantityLevelName', 'SegmentId', 'SegmentName', 'ProjectIssues']) delete record[key];
  return copy;
}
function metricValue(metric, context) {
  assert.ok(metric && Object.hasOwn(metric, 'Value'), `${context}: missing metric`);
  if (metric.Status === 'available') assert.ok(typeof metric.Value === 'number' && Number.isFinite(metric.Value) && metric.Value >= 0, `${context}: invalid available value`);
  else assert.equal(metric.Value, null, `${context}: unavailable/non-applicable value must be null`);
}

export function verifyNative(snapshot, references, levelReferences) {
  const records = indexUnique(snapshot.Elements, 'UniqueId', 'native records');
  assert.equal(records.size, 414);
  equalSet(snapshot.Elements.map(record => record.ElementId), snapshot.Elements.map(record => record.ElementId), 'native ElementIds');
  assert.equal(snapshot.CandidateCount, snapshot.Elements.length + snapshot.Exclusions.length, 'Native candidate coverage is incomplete');
  assert.ok(Array.isArray(snapshot.Notices) && Array.isArray(snapshot.ProjectNotices) && snapshot.ProjectNotices.length === 0);
  assert.ok(typeof snapshot.DocumentScope === 'string' && snapshot.DocumentScope.length > 0 && Number.isFinite(Date.parse(snapshot.CapturedAtUtc)));
  assert.equal(snapshot.LinkInstanceCount, 1); assert.equal(snapshot.MissingLinkCount, 1);
  const counts = {};
  for (const record of records.values()) {
    assert.equal(record.Supported, true); assert.ok(Object.hasOwn(KINDS, record.KindCode));
    counts[record.KindCode] = (counts[record.KindCode] ?? 0) + 1;
    assert.ok(record.PhaseStatus === 'New' || record.PhaseStatus === 'Existing');
    assert.equal(record.QuantityLevelId, ''); assert.equal(record.SegmentId, ''); assert.deepEqual(record.ProjectIssues, []);
    for (const [metric, definition] of Object.entries(METRICS)) {
      metricValue(record[metric], `${record.UniqueId}/${metric}`);
      assert.equal(record[metric].Unit, definition.unit);
      assert.equal(record[metric].Status, applicable(record.KindCode, metric) ? 'available' : 'not-applicable');
      assert.equal(record[metric].Source, applicable(record.KindCode, metric) ? definition.source : '');
    }
  }
  assert.deepEqual(counts, KINDS, 'Civil kind baseline changed');
  assert.equal(references.length, records.size * 3, 'Native metric references must include applicability for every metric/source');
  const nativeMetrics = new Map(), seenMetrics = new Set(); let available = 0;
  for (const reference of references) {
    const record = records.get(reference.uniqueId), definition = METRICS[reference.metric];
    assert.ok(record && definition && record.ElementId === reference.elementId && record.KindCode === reference.kind, 'Native metric reference source mismatch');
    const key = JSON.stringify([reference.uniqueId, reference.metric]); assert.ok(!seenMetrics.has(key), 'Duplicate native metric reference'); seenMetrics.add(key);
    assert.equal(reference.unit, definition.unit);
    const expectedStatus = applicable(record.KindCode, reference.metric) ? 'available' : 'not-applicable';
    assert.equal(reference.status, expectedStatus);
    let value = null;
    if (expectedStatus === 'available') {
      assert.ok(typeof reference.rawValue === 'number' && Number.isFinite(reference.rawValue) && reference.rawValue >= 0, 'Native raw parameter must be finite, nonnegative and present');
      value = reference.rawValue * (0.3048 ** definition.power);
      near(record[reference.metric].Value, value, `Native raw-to-SI ${key}`);
      near(reference.expectedSiValue, value, `Recorded expected SI ${key}`); available++;
    } else {
      assert.equal(reference.rawValue, null); assert.equal(reference.expectedSiValue, null);
    }
    assert.deepEqual(reference.reported, record[reference.metric], 'Reference reported fields differ from the captured snapshot');
    nativeMetrics.set(key, { status: expectedStatus, value, unit: definition.unit, source: record[reference.metric].Source });
  }
  assert.equal(available, 807, 'Expected exactly 807 available native parameter comparisons');

  const levels = indexUnique(snapshot.Levels, 'Id', 'native levels');
  indexUnique(snapshot.Levels, 'UniqueId', 'native level UniqueIds');
  assert.ok(levels.size > 0);
  let previousElevation = -Infinity;
  for (const level of levels.values()) {
    assert.ok(Number.isFinite(level.ElevationMetres) && level.ElevationMetres >= previousElevation, 'Native levels must have finite sorted project elevations');
    previousElevation = level.ElevationMetres;
  }
  const referenceLevels = indexUnique(levelReferences, 'uniqueId', 'native level references');
  equalSet([...referenceLevels.keys()], [...records.keys()], 'Level reference source coverage');
  let beamsRawInvalid = 0, beamsResolved = 0;
  for (const [uid, reference] of referenceLevels) {
    const record = records.get(uid);
    assert.equal(reference.elementId, record.ElementId); assert.equal(reference.kind, record.KindCode);
    assert.equal(reference.rawLevelId, record.RawLevelId); assert.equal(reference.rawLevelName, record.RawLevelName);
    const rawLevel = levels.get(reference.rawLevelId);
    assert.equal(reference.rawLevelName, rawLevel?.Name ?? '');
    assert.deepEqual(reference.explicitNativeParameters.map(item => item.source), PARAMETERS[record.KindCode], 'Wrong category-specific native reference parameters');
    const valid = []; let incomplete = false;
    for (const candidate of reference.explicitNativeParameters) {
      assert.ok(['absent', 'unset', 'invalid-storage', 'not-current-document-level', 'valid'].includes(candidate.status), 'Unknown native reference status');
      if (candidate.status === 'valid') {
        assert.ok(levels.has(candidate.elementId), 'A valid native reference must point to the current document level catalogue'); valid.push(candidate);
      } else if (candidate.status === 'invalid-storage' || candidate.status === 'not-current-document-level') incomplete = true;
      else if (candidate.status === 'absent') assert.equal(candidate.elementId, null);
      else assert.equal(candidate.elementId, '-1');
    }
    const distinct = new Set(valid.map(candidate => candidate.elementId));
    assert.equal(reference.distinctValidReferences, distinct.size); assert.equal(reference.incompleteReference, incomplete);
    const chosen = rawLevel ?? (distinct.size === 1 && !incomplete ? levels.get(valid[0].elementId) : null);
    const source = rawLevel ? 'Element.LevelId' : chosen ? valid[0].source : '';
    assert.equal(record.LevelId, chosen?.Id ?? '-1', 'Resolved native level differs from independent explicit-reference interpretation');
    assert.equal(record.LevelUniqueId, chosen?.UniqueId ?? ''); assert.equal(record.LevelName, chosen?.Name ?? '未指定楼层'); assert.equal(record.LevelSource, source);
    assert.equal(reference.expectedResolvedLevelId, chosen?.Id ?? '-1'); assert.equal(reference.expectedResolvedLevelUniqueId, chosen?.UniqueId ?? ''); assert.equal(reference.expectedSource, source);
    assert.equal(reference.reportedRawLevelId, record.RawLevelId); assert.equal(reference.reportedResolvedLevelId, record.LevelId); assert.equal(reference.reportedSource, record.LevelSource);
    if (record.KindCode === 'beam') {
      assert.equal(record.RawLevelId, '-1'); beamsRawInvalid++;
      assert.ok(chosen && source === 'INSTANCE_REFERENCE_LEVEL_PARAM', 'Beam fallback must use its explicit native reference level'); beamsResolved++;
    }
  }
  assert.equal(beamsRawInvalid, 370); assert.equal(beamsResolved, 370);
  return { records, levels, nativeMetrics, counts, available, beamsRawInvalid, beamsResolved };
}

export function verifyProject(native, selection, applied, reloaded, profile, nativeData, report) {
  assert.ok(profile.SchemaVersion === 1 && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/iu.test(profile.ProjectId));
  assert.equal(profile.SourceDocumentScope, native.DocumentScope); assert.ok(samePath(profile.SourcePath, native.SourcePath));
  assert.equal(profile.PreferredScope, 'project'); assert.equal(profile.DesignOptionId, '-1'); assert.ok(typeof profile.PhaseId === 'string' && profile.PhaseId.length > 0);
  assert.equal(profile.Name, 'SDK acceptance quantity project reloaded'); assert.ok(Number.isSafeInteger(profile.Revision) && profile.Revision > 1);
  const businessLevels = indexUnique(profile.QuantityLevels, 'Id', 'quantity levels');
  const mappings = indexUnique(profile.LevelMappings, 'SourceLevelUniqueId', 'native-to-quantity mappings');
  equalSet([...mappings.keys()], [...nativeData.levels.values()].map(level => level.UniqueId), 'Every current native level must be mapped exactly once');
  assert.equal(businessLevels.size, nativeData.levels.size);
  for (const level of nativeData.levels.values()) {
    const mapping = mappings.get(level.UniqueId), target = businessLevels.get(mapping.QuantityLevelId);
    assert.ok(target && target.Id === `Q-${level.Id}`); assert.equal(target.Name, `计量层 ${level.Name}`);
    near(target.ElevationMetres, level.ElevationMetres, 'Quantity level reference elevation differs from native catalogue');
  }
  const segments = indexUnique(profile.Segments, 'Id', 'segments');
  equalSet([...segments.keys()], ['SEG-A', 'SEG-B'], 'Declared segments');
  assert.equal(segments.get('SEG-A').Name, '施工段 A'); assert.equal(segments.get('SEG-B').Name, '施工段 B');
  const selected = indexUnique(selection.Elements, 'UniqueId', 'native selection');
  assert.equal(selected.size, 2); equalSet([...selected.values()].map(row => row.KindCode), ['wall', 'beam'], 'Selected business kinds');
  assert.equal(selection.DocumentScope, native.DocumentScope); assert.ok(samePath(selection.SourcePath, native.SourcePath));
  assert.equal(selection.PhaseLabel, native.PhaseLabel); assert.equal(selection.DesignOptionLabel, native.DesignOptionLabel);
  for (const [uid, row] of selected) assert.deepEqual(row, nativeData.records.get(uid), 'Selected source differs from the native project record');
  const assignments = indexUnique(profile.ElementSegments, 'ElementUniqueId', 'source element assignments');
  assert.equal(assignments.size, 379); let countA = 0, countB = 0;
  for (const record of nativeData.records.values()) {
    const expected = selected.has(record.UniqueId) ? 'SEG-A' : record.KindCode === 'wall' || record.KindCode === 'beam' ? 'SEG-B' : '';
    assert.equal(assignments.get(record.UniqueId)?.SegmentId ?? '', expected, 'Segment assignment differs from explicit selected/rest source membership');
    if (expected === 'SEG-A') countA++; else if (expected === 'SEG-B') countB++;
  }
  for (const uid of assignments.keys()) assert.ok(nativeData.records.has(uid), 'Assignment invented a foreign source identity');
  assert.equal(countA, 2); assert.equal(countB, 377);
  assert.deepEqual(nativeFacts(applied), nativeFacts(native), 'Apply changed captured native identities, levels, metrics or scope');
  assert.deepEqual(nativeFacts(reloaded), nativeFacts(native), 'Save/reload changed captured native identities, levels, metrics or scope');
  for (const [snapshot, name, revision] of [[applied, 'SDK acceptance quantity project', profile.Revision - 1], [reloaded, profile.Name, profile.Revision]]) {
    assert.equal(snapshot.ProjectId, profile.ProjectId); assert.equal(snapshot.ProjectName, name); assert.equal(snapshot.ProjectRevision, revision);
    equalSet(snapshot.Elements.map(row => row.UniqueId), [...nativeData.records.keys()], 'Annotated source coverage');
    let unassigned = 0;
    for (const record of snapshot.Elements) {
      const level = businessLevels.get(mappings.get(record.LevelUniqueId)?.QuantityLevelId), segment = segments.get(assignments.get(record.UniqueId)?.SegmentId);
      assert.equal(record.QuantityLevelId, level?.Id ?? ''); assert.equal(record.QuantityLevelName, level?.Name ?? '未映射计量层');
      assert.equal(record.SegmentId, segment?.Id ?? ''); assert.equal(record.SegmentName, segment?.Name ?? '未分配施工段');
      assert.deepEqual(record.ProjectIssues, segment ? [] : ['施工段未分配（工程设置）'], 'Unassigned project issue must remain explicit and separate from native issues');
      if (!segment) unassigned++;
    }
    assert.equal(unassigned, 35); assert.equal(snapshot.ProjectNotices.length, 1);
    assert.ok(snapshot.ProjectNotices[0].includes('35') && snapshot.ProjectNotices[0].includes('施工段'));
  }
  assert.equal(report.project.id, profile.ProjectId); assert.equal(report.project.revision, profile.Revision); assert.equal(report.project.schemaVersion, 1);
  assert.equal(report.project.sourceDocumentScope, profile.SourceDocumentScope); assert.ok(samePath(report.project.sourcePath, profile.SourcePath));
  assert.equal(report.project.quantityLevelCount, businessLevels.size); assert.equal(report.project.levelMappingCount, mappings.size);
  assert.equal(report.project.segmentCount, 2); assert.equal(report.project.assignmentCount, assignments.size);
  return { id: profile.ProjectId, revision: profile.Revision, quantityLevels: businessLevels.size, levelMappings: mappings.size, selectedAssigned: countA, remainingWallBeamAssigned: countB, unassigned: 35 };
}

export async function verifyCsv(file, mode, snapshot, nativeData, profile) {
  const id = await identity(file), bytes = await readFile(file);
  assert.ok(bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf, 'CSV must retain UTF-8 BOM');
  const rows = parseCsv(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  const headerAt = rows.findIndex(row => row[0] === '分组标识'); assert.ok(headerAt >= 0);
  const header = rows[headerAt]; assert.equal(header.length, 46); assert.equal(new Set(header).size, header.length);
  assert.deepEqual(header.slice(0, 10), ['分组标识', '构件/分组', '业务类别', '原始楼层', '记录数', '受支持记录数', '未计量记录数', '来源ElementIds', '来源UniqueIds', '问题/未计量原因']);
  const cell = (row, name) => { const at = header.indexOf(name); assert.ok(at >= 0, `Missing CSV column: ${name}`); return row[at]; };
  const metadata = rows.slice(0, headerAt);
  const meta = name => { const values = metadata.filter(row => row[0] === name); assert.equal(values.length, 1, `Missing/duplicate CSV metadata: ${name}`); assert.equal(values[0].length, 2); return values[0][1]; };
  for (const [name, value] of [['来源文档标识', snapshot.DocumentScope], ['来源文件路径', snapshot.SourcePath], ['计量工程标识', profile.ProjectId], ['计量工程名称', profile.Name], ['计量工程修订号', String(profile.Revision)], ['当前记录数', '414'], ['当前受支持记录数', '414']]) assert.equal(meta(name), protectedText(value));
  const separator = rows.findIndex((row, at) => at > headerAt && row.length === 1 && row[0] === '');
  assert.ok(separator > headerAt, 'Missing CSV exclusion-list separator');
  const table = rows.slice(headerAt + 1, separator);
  const groups = new Map();
  const groupKey = row => JSON.stringify(mode === 'element' ? [row.UniqueId] : [mode === 'quantity-level' ? row.QuantityLevelId : row.SegmentId, row.KindCode]);
  for (const record of snapshot.Elements) { const key = groupKey(record); if (!groups.has(key)) groups.set(key, []); groups.get(key).push(record); }
  assert.equal(table.length, groups.size, 'CSV group count differs from independent source grouping');
  const recordMap = indexUnique(snapshot.Elements, 'UniqueId', 'CSV sources'), seen = new Set(), allMembers = [];
  for (const row of table) {
    assert.equal(row.length, header.length, 'CSV row width changed');
    const ids = cell(row, '来源UniqueIds').split('\n'); assert.ok(ids.length > 0 && ids.every(uid => recordMap.has(uid)), 'CSV invented a source identity');
    const first = recordMap.get(ids[0]), key = groupKey(first), expectedMembers = groups.get(key);
    assert.ok(!seen.has(key), 'CSV repeated a group'); seen.add(key);
    equalSet(ids, expectedMembers.map(record => record.UniqueId), 'CSV group membership'); allMembers.push(...ids);
    const members = ids.map(uid => recordMap.get(uid));
    assert.equal(cell(row, '来源ElementIds'), members.map(record => record.ElementId).join('\n'), 'CSV source ElementIds must align with source UniqueIds');
    assert.equal(cell(row, '记录数'), String(members.length)); assert.equal(cell(row, '受支持记录数'), String(members.length)); assert.equal(cell(row, '未计量记录数'), '0');
    assert.equal(cell(row, '业务类别'), protectedText(first.KindName));
    assert.equal(cell(row, '计量工程标识'), profile.ProjectId); assert.equal(cell(row, '计量工程名称'), protectedText(profile.Name)); assert.equal(cell(row, '计量工程修订号'), String(profile.Revision));
    const join = property => members.map(record => record[property] ?? '').join('\n');
    for (const [name, property] of [['计量层标识', 'QuantityLevelId'], ['施工段标识', 'SegmentId'], ['原始Element.LevelId', 'RawLevelId'], ['原始Element.LevelName', 'RawLevelName'], ['参照层UniqueId', 'LevelUniqueId'], ['参照层解析来源', 'LevelSource']]) assert.equal(cell(row, name), protectedText(join(property)), `CSV ${name} differs from its source members`);
    if (mode === 'quantity-level') assert.equal(cell(row, '计量层'), protectedText(first.QuantityLevelName));
    if (mode === 'segment') assert.equal(cell(row, '施工段'), protectedText(first.SegmentName));
    for (const [metric, definition] of Object.entries(METRICS)) {
      const items = members.map(record => nativeData.nativeMetrics.get(JSON.stringify([record.UniqueId, metric])));
      assert.ok(items.every(Boolean));
      const valid = items.filter(item => item.status === 'available');
      const actual = cell(row, `原生${definition.label}(${definition.unit})`);
      if (valid.length) {
        assert.ok(actual.trim().length > 0 && !Number.isNaN(Number(actual)), 'CSV missing numeric total must not become zero');
        near(Number(actual), valid.reduce((sum, item) => sum + item.value, 0), `CSV raw native ${mode}/${metric}/${key}`);
      } else assert.equal(actual, 'NA', 'Non-applicable CSV metric became a numeric value');
      for (const [status, label] of [['available', '有效数'], ['missing', '缺失数'], ['invalid', '无效数'], ['unavailable', '读取失败数'], ['not-applicable', '不适用数']]) assert.equal(cell(row, definition.label + label), String(items.filter(item => item.status === status).length), 'CSV metric coverage differs from independently captured native applicability');
      assert.equal(cell(row, definition.label + '合计溢出'), '');
      const expectedSources = members.map((record, index) => `ElementId=${record.ElementId}; UniqueId=${record.UniqueId}; ${items[index].status}; unit=${definition.unit}; source=${items[index].source}`).join('\n');
      assert.equal(cell(row, definition.label + '来源与状态'), protectedText(expectedSources), 'CSV metric provenance differs from source membership');
    }
  }
  assert.equal(seen.size, groups.size); equalSet(allMembers, snapshot.Elements.map(record => record.UniqueId), 'CSV full source membership');
  const tail = rows.slice(separator + 1);
  assert.deepEqual(tail.slice(0, 2), [['原始读取范围排除清单（不参与量表合计）'], ['ElementId', '名称', '原因']]);
  assert.deepEqual(tail.slice(2), snapshot.Exclusions.map(exclusion => [exclusion.ElementId, exclusion.Name, exclusion.Reason].map(protectedText)), 'CSV omitted or changed explicit scope exclusions');
  return { mode, rows: table.length, columns: header.length, sourceCount: allMembers.length, excludedCount: snapshot.Exclusions.length, ...id };
}

async function validate(runDirectory, options) {
  const run = path.resolve(runDirectory), output = path.join(run, 'output');
  const configInput = await jsonFile(path.join(run, 'config.json')), launchInput = await jsonFile(path.join(run, 'launch.json'));
  const hostInput = await jsonFile(path.join(output, 'civil-project-host-report.json'));
  const config = configInput.data, launch = launchInput.data, report = hostInput.data;
  assert.ok(/^[a-f\d]{32}$/u.test(config.runId) && path.basename(run) === `run-${config.runId}`);
  assert.equal(report.result, 'PASS', 'SDK host did not complete successfully');
  assert.equal(report.runId, config.runId); assert.equal(launch.runId, config.runId); assert.equal(report.processId, launch.processId); assert.equal(report.revitYear, '2020');
  assert.ok(samePath(config.outputDirectory, output) && samePath(report.configurationPath, configInput.identity.path) && samePath(launch.configPath, configInput.identity.path));
  assert.equal(report.configurationSha256, configInput.identity.sha256); assert.equal(launch.configSha256, configInput.identity.sha256);
  for (const flag of ['startupNonceConsumed', 'independentNativeMetricsVerified', 'independentNativeLevelsVerified', 'nativeSelectionVerified', 'nativeMetricsPreservedByApply', 'csvIndependentNativeCounterpartVerified', 'liveRevitProjectSdkVerified', 'selectionRestored', 'activeViewUnchanged']) assert.equal(report[flag], true, `Missing host acceptance assertion: ${flag}`);
  for (const flag of ['sourceSaved', 'transactionsStarted', 'ribbonClickVerified', 'nativeWorkbenchWindowVerified', 'nativeProjectWindowVerified']) assert.equal(report[flag], false, `SDK test exceeded its declared scope: ${flag}`);
  assert.equal(launch.sourceSaved, false); assert.equal(launch.transactionsAllowed, false); assert.equal(launch.activeViewWritesAllowed, false);
  assert.equal(report.activeViewBefore.id, report.activeViewAfter.id); assert.equal(report.activeViewBefore.name, report.activeViewAfter.name);
  assert.ok(report.elapsedSeconds > 0 && Number.isFinite(report.elapsedSeconds) && Number.isSafeInteger(report.assertions) && report.assertions > 0);
  const finished = Date.parse(report.finishedUtc), ready = Date.parse(report.sdkReadyUtc); assert.ok(Number.isFinite(finished) && Number.isFinite(ready) && finished >= ready);

  const productionPath = path.resolve(options.buildManifest ?? path.join(MODULE, 'artifacts', 'BV-1.6.0-final', 'build-manifest.json'));
  assert.ok(samePath(productionPath, launch.productionBuildManifest));
  const buildInput = await jsonFile(productionPath), build = buildInput.data;
  assert.equal(buildInput.identity.sha256, launch.productionBuildManifestSha256);
  assert.equal(build.packageId, 'BrightVision.RevitExporter'); assert.equal(build.packageVersion, '1.6.0'); assert.equal(build.fullClassName, 'BrightVision.Revit.App');
  assert.equal(build.sourceFiles.length, 19); assert.equal(build.variants.length, 14);
  equalSet(build.sourceFiles.map(file => file.name), [...build.sourceFiles.map(file => file.name)], 'Frozen production source names');
  const sourceFiles = [];
  for (const file of build.sourceFiles) sourceFiles.push({ name: file.name, ...await hashMatches(child(MODULE, file.name), file.sha256) });
  const expectedVariants = [];
  for (let year = 2015; year <= 2026; year++) for (const runtime of year >= 2025 ? ['net8', 'net10'] : [year >= 2021 ? 'net48' : 'net45']) expectedVariants.push(`${year}-${runtime}`);
  equalSet(build.variants.map(variant => variant.id), expectedVariants, 'Frozen target matrix');
  const payloads = [];
  for (const variant of build.variants) {
    assert.equal(variant.buildStatus, 'built'); assert.equal(variant.files.length, 1); assert.equal(variant.files[0].name, 'Bright.Vision.Revit.dll');
    assert.equal(variant.relativePath, `payload/${variant.id}/Bright.Vision.Revit.dll`);
    payloads.push({ variant: variant.id, ...await hashMatches(path.join(path.dirname(productionPath), ...variant.relativePath.split('/')), variant.files[0].sha256, variant.files[0].size) });
  }
  const target = build.variants.find(variant => variant.id === '2020-net45');
  assert.equal(target.files[0].sha256, config.expectedExporterSha256); assert.equal(launch.exporterSha256, config.expectedExporterSha256);
  assert.equal(report.plugin.version, '1.6.0.0'); assert.ok(samePath(config.exporterPath, launch.exporterPath) && samePath(report.plugin.path, config.exporterPath));
  assert.equal(report.plugin.sha256, config.expectedExporterSha256);
  const plugin = await hashMatches(config.exporterPath, config.expectedExporterSha256, target.files[0].size);
  const installedInput = await jsonFile(launch.installedStatePath), installed = installedInput.data;
  assert.equal(installedInput.identity.sha256, launch.installedStateSha256);
  assert.equal(installed.packageId, build.packageId); assert.equal(installed.packageVersion, '1.6.0'); assert.equal(installed.variantId, '2020-net45'); assert.equal(installed.year, '2020');
  assert.ok(samePath(installed.targetDirectory, path.dirname(config.exporterPath)));
  for (const file of installed.files) await hashMatches(child(installed.targetDirectory, file.name), file.sha256);
  assert.equal(installed.files.find(file => file.name === 'Bright.Vision.Revit.dll')?.sha256, config.expectedExporterSha256);

  const driverInput = await jsonFile(launch.driverBuildManifest), driver = driverInput.data;
  assert.equal(driverInput.identity.sha256, launch.driverBuildManifestSha256); assert.equal(driver.expectedProductionVersion, '1.6.0.0'); assert.equal(driver.testOnly, true);
  assert.equal(driver.assemblySha256, config.expectedDriverSha256); assert.equal(launch.driverSha256, config.expectedDriverSha256); assert.equal(report.driver.sha256, config.expectedDriverSha256);
  assert.ok(samePath(driver.assemblyPath, launch.driver) && samePath(driver.assemblyPath, report.driver.path));
  await hashMatches(driver.assemblyPath, config.expectedDriverSha256); await hashMatches(driver.sourcePath, driver.sourceSha256);
  for (const api of driver.api) await hashMatches(api.path, api.sha256);
  for (const api of target.sourceApi) await hashMatches(api.path, api.sha256);

  assert.ok(samePath(config.modelPath, launch.modelPath) && samePath(config.modelPath, report.source.path));
  assert.equal(config.expectedModelSha256, launch.modelSha256); assert.equal(report.source.expectedSha256, config.expectedModelSha256);
  assert.equal(report.source.sha256Before, config.expectedModelSha256); assert.equal(report.source.sha256After, config.expectedModelSha256); assert.equal(report.source.hashUnchanged, true);
  assert.equal(typeof report.source.isModifiedBefore, 'boolean'); assert.equal(report.source.isModifiedBefore, report.source.isModifiedAfter); assert.equal(report.source.isModifiedUnchanged, true);
  const source = await hashMatches(config.modelPath, config.expectedModelSha256, report.source.bytes);
  const preparedInput = await jsonFile(path.join(run, 'prepared-model.json')), prepared = preparedInput.data;
  assert.ok(samePath(prepared.copy, config.modelPath)); assert.equal(prepared.sourceSaved, false); assert.equal(prepared.sha256, config.expectedModelSha256); assert.equal(prepared.bytes, source.bytes);
  await hashMatches(prepared.original, prepared.sha256, prepared.bytes);

  equalSet(report.artifacts.map(file => file.name), DATA_FILES, 'SDK completed evidence catalogue');
  const artifacts = {};
  for (const file of report.artifacts) artifacts[file.name] = await hashMatches(child(output, file.name), file.sha256, file.bytes);
  const inputs = {};
  for (const file of DATA_FILES.filter(name => name.endsWith('.json'))) inputs[file] = (await jsonFile(child(output, file))).data;
  const native = inputs['native-project-snapshot.json'];
  assert.ok(samePath(native.SourcePath, config.modelPath));
  const nativeData = verifyNative(native, inputs['native-metric-references.json'], inputs['native-level-references.json']);
  assert.equal(report.projectRecordCount, 414); assert.deepEqual(report.supportedKinds, KINDS); assert.equal(report.nativeAvailableComparisons, nativeData.available);
  assert.equal(report.beamRawInvalidLevelCount, nativeData.beamsRawInvalid); assert.equal(report.beamResolvedReferenceLevelCount, nativeData.beamsResolved);
  assert.equal(report.selectedSegmentCount, 2); assert.equal(report.remainingWallBeamSegmentCount, 377); assert.equal(report.unassignedSegmentCount, 35);
  const profile = inputs['quantity-project.json'], reloaded = inputs['project-reloaded-snapshot.json'];
  const project = verifyProject(native, inputs['selection-native-snapshot.json'], inputs['project-applied-snapshot.json'], reloaded, profile, nativeData, report);
  assert.ok(samePath(report.project.path, path.join(output, 'quantity-project.json'))); assert.equal(report.project.reloadedSha256, artifacts['quantity-project.json'].sha256);
  assert.ok(isHash(report.project.firstSavedSha256) && report.project.firstSavedSha256 !== report.project.reloadedSha256); assert.equal(report.project.saveLoadVerified, true); assert.equal(report.project.replaceVerified, true);
  const csv = [];
  equalSet(report.csv.map(item => item.mode), ['quantity-level', 'segment', 'element'], 'SDK CSV modes');
  for (const mode of ['quantity-level', 'segment', 'element']) {
    const file = path.join(output, `project-${mode}.csv`), sdk = report.csv.find(item => item.mode === mode);
    assert.ok(samePath(sdk.path, file)); assert.equal(sdk.sha256, artifacts[`project-${mode}.csv`].sha256); assert.equal(sdk.bytes, artifacts[`project-${mode}.csv`].bytes); assert.equal(sdk.independentNativeCounterpartVerified, true);
    const check = await verifyCsv(file, mode, reloaded, nativeData, profile);
    assert.equal(sdk.rows, check.rows); assert.equal(sdk.columns, check.columns); assert.equal(sdk.sourceCount, check.sourceCount); csv.push(check);
  }
  return {
    result: 'PASS', runId: config.runId, validatedAtUtc: new Date().toISOString(),
    verifier: await identity(fileURLToPath(import.meta.url)), parser: await identity(fileURLToPath(new URL('./verify-civil-host.mjs', import.meta.url))),
    host: { processId: report.processId, year: report.revitYear, build: report.revitBuild, assertions: report.assertions, acceptanceElapsedSeconds: report.elapsedSeconds },
    inputs: { config: configInput.identity, launch: launchInput.identity, hostReport: hostInput.identity, prepared: preparedInput.identity, buildManifest: buildInput.identity, driverBuildManifest: driverInput.identity, installedState: installedInput.identity },
    source, plugin: { ...plugin, version: report.plugin.version }, production: { version: build.packageVersion, sourceFiles, payloads }, artifacts,
    native: { recordCount: nativeData.records.size, kinds: nativeData.counts, applicableComparisons: nativeData.available, metricReferenceRows: inputs['native-metric-references.json'].length,
      rawInvalidBeamLevels: nativeData.beamsRawInvalid, explicitReferenceResolvedBeams: nativeData.beamsResolved, levelCount: nativeData.levels.size,
      metricOracle: 'Host-captured rawValue independently multiplied by 0.3048^dimension in Node; expectedSiValue/reported are checked but not used as the oracle.' },
    project, csv, sourceSaved: false, sourceFileHashUnchanged: true, isModifiedBefore: report.source.isModifiedBefore, isModifiedAfter: report.source.isModifiedAfter,
    ribbonClickVerified: false, nativeWorkbenchWindowVerified: false, nativeProjectWindowVerified: false,
    limitations: ['Independent Node checks read SDK-captured references and files; they do not run another Revit API session or verify native windows.',
      'Selection restoration, unchanged active view, native Level.ProjectElevation correspondence, and first-save/replacement execution remain host-driver assertions bound to this report and frozen driver.',
      'This runtime acceptance covers Revit 2020 and this official sample only; other hosts, architectural columns, review/brace and unavailable-metric runtime cases remain outside this sample.',
      'The elapsed time is the complete SDK acceptance sequence, not a reader/export performance benchmark. Equal IsModified flags do not prove every in-memory property unchanged.',
      'Business levels and segments annotate whole source records; they do not split geometry or establish regional bill/quantity deduction correctness.'],
  };
}

export async function verifyCivilProjectHost(runDirectory, options = {}) {
  const output = path.join(path.resolve(runDirectory), 'output');
  const reportPath = path.join(output, 'civil-project-data-validation.json');
  try {
    const result = await validate(runDirectory, options);
    if (options.writeResult !== false) await writeFile(reportPath, JSON.stringify(result, null, 2), 'utf8');
    return result;
  } catch (error) {
    if (options.writeResult !== false) await writeFile(reportPath, JSON.stringify({ result: 'FAIL', input: path.resolve(runDirectory), error: String(error?.stack ?? error), validatedAtUtc: new Date().toISOString(),
      ribbonClickVerified: false, nativeWorkbenchWindowVerified: false, nativeProjectWindowVerified: false }, null, 2), 'utf8');
    throw error;
  }
}

if (process.argv[1] && samePath(process.argv[1], fileURLToPath(import.meta.url))) {
  const args = process.argv.slice(2), run = args.shift();
  let buildManifest;
  if (args.length) { assert.equal(args.shift(), '--build-manifest'); buildManifest = args.shift(); assert.ok(buildManifest && args.length === 0); }
  if (!run) { console.error('Usage: node tests/verify-civil-project-host.mjs <run-directory> [--build-manifest <frozen-BV-1.6.0-build-manifest.json>]'); process.exitCode = 2; }
  else try {
    const result = await verifyCivilProjectHost(run, { buildManifest });
    console.log(JSON.stringify({ result: result.result, runId: result.runId, native: result.native, project: result.project, csv: result.csv.map(item => ({ mode: item.mode, rows: item.rows, sourceCount: item.sourceCount })), sourceFileHashUnchanged: result.sourceFileHashUnchanged }));
  } catch (error) { console.error(String(error?.stack ?? error)); process.exitCode = 1; }
}
