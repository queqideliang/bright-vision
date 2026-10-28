import assert from 'node:assert/strict';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyNative, verifyProject, verifyCsv } from './verify-civil-project-host.mjs';

// Entirely synthetic contract checks. These are deliberately not a Revit run,
// not copied 1.5 evidence, and never reported as BV 1.6 host acceptance.
const kinds = { wall: 9, floor: 5, 'structural-column': 30, beam: 370 };
const definitions = [['Length', '长度', 'm', 'CURVE_ELEM_LENGTH'], ['Area', '面积', 'm2', 'HOST_AREA_COMPUTED'], ['Volume', '体积', 'm3', 'HOST_VOLUME_COMPUTED']];
const applicable = (kind, metric) => metric === 'Volume' || (metric === 'Length' ? kind === 'wall' || kind === 'beam' : kind === 'wall' || kind === 'floor');
const native = { ModelTitle: 'Synthetic fixture, never a Revit acceptance', DocumentScope: 'synthetic-only', SourcePath: 'H:\\synthetic\\fixture.rvt', CapturedAtUtc: '2026-10-28T00:00:00Z',
  ScopeLabel: 'project', PhaseLabel: 'Synthetic phase', DesignOptionLabel: 'main', Revision: 0, CandidateCount: 415, LinkInstanceCount: 1, MissingLinkCount: 1,
  Notices: ['Synthetic only'], ProjectNotices: [], Elements: [], Exclusions: [{ ElementId: 'excluded', Name: 'Synthetic excluded', Reason: 'Outside supported kinds' }],
  Levels: [{ Id: '1', UniqueId: 'level-1', Name: 'Synthetic 1F', ElevationMetres: 0 }], ProjectId: '', ProjectName: '', ProjectRevision: 0 };
const references = [], levelReferences = [];
for (const [kind, count] of Object.entries(kinds)) for (let number = 0; number < count; number++) {
  const uid = `synthetic-${kind}-${number}`, raw = kind === 'beam' ? '-1' : '1';
  const row = { ElementId: uid, UniqueId: uid, Name: uid, KindCode: kind, KindName: kind, Supported: true, ExclusionReason: '', PhaseStatus: 'New',
    RawLevelId: raw, RawLevelName: raw === '-1' ? '' : 'Synthetic 1F', LevelId: '1', LevelName: 'Synthetic 1F', LevelUniqueId: 'level-1',
    LevelSource: raw === '-1' ? 'INSTANCE_REFERENCE_LEVEL_PARAM' : 'Element.LevelId', QuantityLevelId: '', QuantityLevelName: '', SegmentId: '', SegmentName: '', Issues: [], ProjectIssues: [] };
  for (const [index, [metric, , unit, source]] of definitions.entries()) {
    const active = applicable(kind, metric), value = active ? 10 * 0.3048 ** (index + 1) : null;
    row[metric] = { Status: active ? 'available' : 'not-applicable', Value: value, Unit: unit, Source: active ? source : '' };
    references.push({ elementId: uid, uniqueId: uid, kind, metric, status: row[metric].Status, unit, rawValue: active ? 10 : null, expectedSiValue: value, reported: structuredClone(row[metric]) });
  }
  const parameters = kind === 'wall' ? ['WALL_BASE_CONSTRAINT'] : kind === 'floor' ? ['LEVEL_PARAM'] : kind === 'beam'
    ? ['INSTANCE_REFERENCE_LEVEL_PARAM', 'FAMILY_LEVEL_PARAM', 'SCHEDULE_LEVEL_PARAM'] : ['FAMILY_BASE_LEVEL_PARAM', 'FAMILY_LEVEL_PARAM', 'SCHEDULE_LEVEL_PARAM'];
  levelReferences.push({ elementId: uid, uniqueId: uid, kind, rawLevelId: raw, rawLevelName: row.RawLevelName,
    explicitNativeParameters: parameters.map((source, index) => ({ source, status: index ? 'absent' : 'valid', elementId: index ? null : '1' })),
    distinctValidReferences: 1, incompleteReference: false, expectedResolvedLevelId: '1', expectedResolvedLevelUniqueId: 'level-1', expectedSource: row.LevelSource,
    reportedRawLevelId: raw, reportedResolvedLevelId: '1', reportedSource: row.LevelSource });
  native.Elements.push(row);
}
const data = verifyNative(native, references, levelReferences);
assert.equal(data.available, 807);
const chosen = [native.Elements.find(row => row.KindCode === 'wall'), native.Elements.find(row => row.KindCode === 'beam')];
const selected = new Set(chosen.map(row => row.UniqueId));
const selection = { ...structuredClone(native), Elements: structuredClone(chosen) };
const profile = { SchemaVersion: 1, ProjectId: '11111111-1111-1111-1111-111111111111', Name: 'SDK acceptance quantity project reloaded', Revision: 5,
  SourceDocumentScope: native.DocumentScope, SourcePath: native.SourcePath, PreferredScope: 'project', DesignOptionId: '-1', PhaseId: '1',
  QuantityLevels: [{ Id: 'Q-1', Name: '计量层 Synthetic 1F', ElevationMetres: 0 }], LevelMappings: [{ SourceLevelUniqueId: 'level-1', QuantityLevelId: 'Q-1' }],
  Segments: [{ Id: 'SEG-A', Name: '施工段 A' }, { Id: 'SEG-B', Name: '施工段 B' }], ElementSegments: [] };
for (const row of native.Elements) if (row.KindCode === 'wall' || row.KindCode === 'beam') profile.ElementSegments.push({ ElementUniqueId: row.UniqueId, SegmentId: selected.has(row.UniqueId) ? 'SEG-A' : 'SEG-B' });
const final = structuredClone(native);
Object.assign(final, { ProjectId: profile.ProjectId, ProjectName: profile.Name, ProjectRevision: 5, ProjectNotices: ['35 个当前范围构件尚未分配施工段；未执行几何切分。'] });
for (const row of final.Elements) {
  row.QuantityLevelId = 'Q-1'; row.QuantityLevelName = profile.QuantityLevels[0].Name;
  row.SegmentId = profile.ElementSegments.find(item => item.ElementUniqueId === row.UniqueId)?.SegmentId ?? '';
  row.SegmentName = profile.Segments.find(item => item.Id === row.SegmentId)?.Name ?? '未分配施工段';
  row.ProjectIssues = row.SegmentId ? [] : ['施工段未分配（工程设置）'];
}
const applied = structuredClone(final); applied.ProjectName = 'SDK acceptance quantity project'; applied.ProjectRevision = 4;
const report = { project: { id: profile.ProjectId, revision: 5, schemaVersion: 1, sourceDocumentScope: native.DocumentScope, sourcePath: native.SourcePath, quantityLevelCount: 1, levelMappingCount: 1, segmentCount: 2, assignmentCount: 379 } };
verifyProject(native, selection, applied, final, profile, data, report);
let positive = 2, negative = 0;
function rejects(label, mutate, action) { const value = mutate(); assert.throws(() => action(value), { name: 'AssertionError' }, label); negative++; }
rejects('Changed metric must fail raw SI validation', () => { const copy = structuredClone(native); copy.Elements[0].Length.Value += 1; return copy; }, copy => verifyNative(copy, references, levelReferences));
rejects('Lost beam raw level must fail', () => { const copy = structuredClone(native); copy.Elements.find(row => row.KindCode === 'beam').RawLevelId = '1'; return copy; }, copy => verifyNative(copy, references, levelReferences));
rejects('Invented geometry-derived source must fail', () => { const copy = structuredClone(native); copy.Elements.find(row => row.KindCode === 'beam').LevelSource = 'bounding-box'; return copy; }, copy => verifyNative(copy, references, levelReferences));
rejects('Source assignment must not silently change', () => { const copy = structuredClone(profile); copy.ElementSegments[0].SegmentId = 'SEG-B'; return copy; }, copy => verifyProject(native, selection, applied, final, copy, data, report));
rejects('Annotation must not alter native amounts', () => { const copy = structuredClone(final); copy.Elements[0].Volume.Value += 1; return copy; }, copy => verifyProject(native, selection, applied, copy, profile, data, report));

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'artifacts', 'tests'); await mkdir(root, { recursive: true });
const output = await mkdtemp(path.join(root, 'civil-project-verifier-synthetic-'));
const protect = text => (/^[\s\p{Cc}\ufeff]*[=+\-@]/u.test(String(text)) || /^[\t\r\n]/u.test(String(text))) ? `'${text}` : String(text);
const line = cells => cells.map(value => `"${protect(value ?? '').replaceAll('"', '""')}"`).join(',') + '\r\n';
for (const mode of ['quantity-level', 'segment', 'element']) {
  const header = ['分组标识', '构件/分组', '业务类别', '原始楼层', '记录数', '受支持记录数', '未计量记录数', '来源ElementIds', '来源UniqueIds', '问题/未计量原因'];
  for (const [, label, unit] of definitions) header.push(`原生${label}(${unit})`, ...['有效数', '缺失数', '无效数', '读取失败数', '不适用数', '合计溢出', '来源与状态'].map(suffix => label + suffix));
  header.push('计量工程标识', '计量工程名称', '计量工程修订号', '计量层标识', '计量层', '施工段标识', '施工段', '原始Element.LevelId', '原始Element.LevelName', '参照层UniqueId', '参照层解析来源', '工程设置问题');
  const groups = new Map();
  for (const row of final.Elements) {
    const key = JSON.stringify(mode === 'element' ? [row.UniqueId] : [mode === 'segment' ? row.SegmentId : row.QuantityLevelId, row.KindCode]);
    if (!groups.has(key)) groups.set(key, []); groups.get(key).push(row);
  }
  let csv = [['来源文档标识', final.DocumentScope], ['来源文件路径', final.SourcePath], ['计量工程标识', profile.ProjectId], ['计量工程名称', profile.Name], ['计量工程修订号', 5], ['当前记录数', 414], ['当前受支持记录数', 414]].map(line).join('') + line(header);
  const rows = [];
  for (const [key, members] of groups) {
    const first = members[0], join = field => members.map(row => row[field]).join('\n');
    const row = [key, 'Synthetic group', first.KindName, first.LevelName, members.length, members.length, 0, members.map(item => item.ElementId).join('\n'), members.map(item => item.UniqueId).join('\n'), ''];
    for (const [metric, , unit] of definitions) {
      const valid = members.filter(item => item[metric].Status === 'available');
      row.push(valid.length ? valid.reduce((sum, item) => sum + item[metric].Value, 0) : 'NA', valid.length, 0, 0, 0, members.length - valid.length, '', members.map(item => `ElementId=${item.ElementId}; UniqueId=${item.UniqueId}; ${item[metric].Status}; unit=${unit}; source=${item[metric].Source}`).join('\n'));
    }
    row.push(profile.ProjectId, profile.Name, 5, join('QuantityLevelId'), first.QuantityLevelName, join('SegmentId'), first.SegmentName, join('RawLevelId'), join('RawLevelName'), join('LevelUniqueId'), join('LevelSource'), '');
    rows.push(row); csv += line(row);
  }
  const tail = line([]) + line(['原始读取范围排除清单（不参与量表合计）']) + line(['ElementId', '名称', '原因']) + native.Exclusions.map(item => line([item.ElementId, item.Name, item.Reason])).join('');
  const file = path.join(output, `synthetic-${mode}.csv`); await writeFile(file, '\ufeff' + csv + tail);
  await verifyCsv(file, mode, final, data, profile); positive++;
  if (mode === 'quantity-level') {
    const broken = structuredClone(rows); broken[0][10] = 123;
    const prefix = csv.slice(0, csv.indexOf(line(rows[0])));
    await writeFile(file, '\ufeff' + prefix + broken.map(line).join('') + tail);
    await assert.rejects(verifyCsv(file, mode, final, data, profile), { name: 'AssertionError' }); negative++;
    broken[0] = structuredClone(rows[0]); const ids = broken[0][8].split('\n'); ids[1] = ids[0]; broken[0][8] = ids.join('\n');
    await writeFile(file, '\ufeff' + prefix + broken.map(line).join('') + tail);
    await assert.rejects(verifyCsv(file, mode, final, data, profile), { name: 'AssertionError' }); negative++;
  }
}
const summary = { result: 'PASS', scope: 'SYNTHETIC CONTRACT ONLY. Does not establish BV 1.6 actual Revit runtime acceptance.', positiveChecks: positive, rejectedTamperCases: negative, output };
await writeFile(path.join(output, 'synthetic-validation.json'), JSON.stringify(summary, null, 2)); console.log(JSON.stringify(summary));
