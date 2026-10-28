import type { BimComponent } from './model.ts';

export const QUANTITY_KEYS = ['length', 'area', 'volume'] as const;
export type QuantityKey = typeof QUANTITY_KEYS[number];
export type QuantityStatus = 'available' | 'missing' | 'invalid' | 'unavailable';
export type QuantityMode = 'component' | 'category' | 'level';
export type QuantityLanguage = 'zh' | 'en';
export const QUANTITY_DEFINITIONS = Object.freeze({
  length: { unit: 'm', parameter: 'CURVE_ELEM_LENGTH' },
  area: { unit: 'm2', parameter: 'HOST_AREA_COMPUTED' },
  volume: { unit: 'm3', parameter: 'HOST_VOLUME_COMPUTED' },
} as const);

export interface QuantitySourceElement {
  uniqueId: string;
  elementId: string;
  documentScope: string;
  linkInstanceScope: string;
}
export interface QuantityMetric {
  status: QuantityStatus;
  value: number | null;
  unit: 'm' | 'm2' | 'm3';
  parameter: string;
  issue?: string;
}
export interface QuantitySnapshot {
  schemaVersion: 1;
  source: 'revit-parameters';
  sourceElement: QuantitySourceElement;
  metrics: Record<QuantityKey, QuantityMetric>;
}
export interface QuantityMetricSummary {
  value: number | null;
  available: number;
  missing: number;
  invalid: number;
  unavailable: number;
  overflow: boolean;
}
export interface QuantityRow {
  key: string;
  label: string;
  componentIds: string[];
  count: number;
  recordCount: number;
  duplicateSources: number;
  category: string;
  level: string;
  sources: QuantitySourceElement[];
  metrics: Record<QuantityKey, QuantityMetricSummary>;
}

const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
const identityText = (value: unknown, empty = false): value is string => typeof value === 'string'
  && value.length <= 1024 && ((empty && value === '') || value.trim().length > 0) && !/[\u0000-\u001f\u007f]/u.test(value);

function metricStatus(key: QuantityKey, status: QuantityStatus, issue?: string): QuantityMetric {
  return { ...QUANTITY_DEFINITIONS[key], status, value: null, ...(issue ? { issue } : {}) };
}

/** An invalid quantity field never makes the display model unusable or becomes a zero quantity. */
export function parseQuantitySnapshot(raw: unknown): { snapshot?: QuantitySnapshot; issue?: string } {
  if (raw === undefined) return {};
  if (!object(raw)) return { issue: 'invalid-snapshot' };
  if (raw.schemaVersion !== 1) return { issue: 'unsupported-version' };
  if (raw.source !== 'revit-parameters') return { issue: 'invalid-source' };
  const source = raw.sourceElement;
  if (!object(source) || !identityText(source.uniqueId) || !identityText(source.elementId)
    || !identityText(source.documentScope) || !identityText(source.linkInstanceScope, true)) return { issue: 'invalid-source-element' };
  if (!object(raw.metrics)) return { issue: 'invalid-metrics' };
  const metrics = {} as Record<QuantityKey, QuantityMetric>;
  let invalid = false;
  for (const key of QUANTITY_KEYS) {
    const value = raw.metrics[key];
    const expected = QUANTITY_DEFINITIONS[key];
    if (!object(value) || value.unit !== expected.unit || value.parameter !== expected.parameter) {
      metrics[key] = metricStatus(key, 'invalid', 'invalid-metric-definition'); invalid = true; continue;
    }
    if (value.status === 'available') {
      if (typeof value.value === 'number' && Number.isFinite(value.value) && value.value >= 0) {
        metrics[key] = { ...expected, status: 'available', value: value.value };
      } else { metrics[key] = metricStatus(key, 'invalid', 'invalid-number'); invalid = true; }
    } else if ((value.status === 'missing' || value.status === 'invalid' || value.status === 'unavailable') && value.value === null) {
      metrics[key] = metricStatus(key, value.status);
    } else { metrics[key] = metricStatus(key, 'invalid', 'invalid-status-value'); invalid = true; }
  }
  return {
    snapshot: {
      schemaVersion: 1, source: 'revit-parameters',
      sourceElement: { uniqueId: source.uniqueId, elementId: source.elementId, documentScope: source.documentScope, linkInstanceScope: source.linkInstanceScope },
      metrics,
    },
    ...(invalid ? { issue: 'invalid-metric' } : {}),
  };
}

interface QuantityObject {
  ordinal: number;
  key: string;
  label: string;
  componentIds: string[];
  categories: Set<string>;
  levels: Set<string>;
  source?: QuantitySourceElement;
  metrics: Record<QuantityKey, QuantityMetric>;
}

function sourceKey(source: QuantitySourceElement): string {
  return `source:${JSON.stringify([source.documentScope, source.linkInstanceScope, source.uniqueId, source.elementId])}`;
}

/** A Revit element can produce several scene records; these are one quantity object per link placement. */
function mergeQuantityObjects(universe: readonly BimComponent[]): QuantityObject[] {
  const objects = new Map<string, QuantityObject>();
  const sceneIds = new Set<string>();
  for (const component of universe) {
    if (sceneIds.has(component.id)) continue;
    sceneIds.add(component.id);
    const snapshot = component.quantitySnapshot;
    const key = snapshot ? sourceKey(snapshot.sourceElement) : `record:${JSON.stringify(component.id)}`;
    const metrics = snapshot?.metrics ?? Object.fromEntries(QUANTITY_KEYS.map(metric => [metric, metricStatus(metric, component.quantityIssue ? 'invalid' : 'missing')])) as Record<QuantityKey, QuantityMetric>;
    const existing = objects.get(key);
    if (existing) {
      existing.componentIds.push(component.id);
      existing.categories.add(component.category);
      for (const level of component.levelNames) if (level) existing.levels.add(level);
      for (const metric of QUANTITY_KEYS) {
        const left = existing.metrics[metric]; const right = metrics[metric];
        if (left.issue === 'conflicting-source') continue;
        if (left.status !== right.status || left.value !== right.value || left.unit !== right.unit || left.parameter !== right.parameter) {
          existing.metrics[metric] = metricStatus(metric, 'invalid', 'conflicting-source');
        }
      }
    } else {
      objects.set(key, {
        ordinal: objects.size, key, label: component.name || component.id, componentIds: [component.id], categories: new Set([component.category]),
        levels: new Set(component.levelNames.filter(Boolean)), source: snapshot ? { ...snapshot.sourceElement } : undefined,
        metrics: Object.fromEntries(QUANTITY_KEYS.map(metric => [metric, { ...metrics[metric] }])) as Record<QuantityKey, QuantityMetric>,
      });
    }
  }
  return [...objects.values()];
}

function labels(language: QuantityLanguage) {
  return language === 'en'
    ? { mixedCategory: 'Conflicting categories', noCategory: 'Unclassified', multiLevel: 'Multiple levels (not split)', noLevel: 'Unassigned level', total: 'Total' }
    : { mixedCategory: '类别不一致', noCategory: '未分类', multiLevel: '多楼层（未拆分）', noLevel: '未指定楼层', total: '合计' };
}
function categoryOf(item: QuantityObject, language: QuantityLanguage): string {
  return item.categories.size > 1 ? labels(language).mixedCategory : ([...item.categories][0] || labels(language).noCategory);
}
function levelOf(item: QuantityObject, language: QuantityLanguage): string {
  return item.levels.size > 1 ? labels(language).multiLevel : ([...item.levels][0] || labels(language).noLevel);
}

function summarizeMetric(objects: readonly QuantityObject[], key: QuantityKey): QuantityMetricSummary {
  const result: QuantityMetricSummary = { value: null, available: 0, missing: 0, invalid: 0, unavailable: 0, overflow: false };
  let sum = 0; let correction = 0;
  for (const object of objects) {
    const metric = object.metrics[key]; result[metric.status]++;
    if (metric.status !== 'available') continue;
    const value = metric.value!;
    const next = sum + value;
    if (!Number.isFinite(next)) { result.overflow = true; continue; }
    // Neumaier summation retains small contributions when element quantities differ greatly.
    correction += Math.abs(sum) >= Math.abs(value) ? (sum - next) + value : (value - next) + sum;
    sum = next;
  }
  const total = sum + correction;
  if (!Number.isFinite(total)) result.overflow = true;
  if (result.available && !result.overflow) result.value = total;
  return result;
}

function row(key: string, label: string, objects: readonly QuantityObject[], category: string, level: string): QuantityRow {
  const componentIds = objects.flatMap(object => object.componentIds);
  return {
    key, label, componentIds, count: objects.length, recordCount: componentIds.length,
    duplicateSources: componentIds.length - objects.length, category, level,
    sources: objects.flatMap(object => object.source ? [{ ...object.source }] : []),
    metrics: Object.fromEntries(QUANTITY_KEYS.map(metric => [metric, summarizeMetric(objects, metric)])) as Record<QuantityKey, QuantityMetricSummary>,
  };
}

function rowsForObjects(objects: readonly QuantityObject[], mode: QuantityMode, language: QuantityLanguage): QuantityRow[] {
  if (mode === 'component') return objects.map(object => row(object.key, object.label, [object], categoryOf(object, language), levelOf(object, language)));
  const groups = new Map<string, { label: string; objects: QuantityObject[] }>();
  for (const object of objects) {
    const label = mode === 'category' ? categoryOf(object, language) : levelOf(object, language);
    const values = mode === 'category' ? object.categories : object.levels;
    const key = values.size > 1 ? `${mode}:multiple` : values.size === 0 || [...values][0] === '' ? `${mode}:unassigned` : `${mode}:${JSON.stringify([...values][0])}`;
    const group = groups.get(key);
    if (group) group.objects.push(object); else groups.set(key, { label, objects: [object] });
  }
  return [...groups].map(([key, group]) => row(key, group.label, group.objects, mode === 'category' ? group.label : '', mode === 'level' ? group.label : ''));
}

export interface QuantityIndex {
  buildRows(scope: readonly BimComponent[], mode: QuantityMode, language?: QuantityLanguage): QuantityRow[];
  summarize(scope: readonly BimComponent[], language?: QuantityLanguage): QuantityRow;
}

/** Build once per loaded model, then query scopes without merging the full model again. */
export function createQuantityIndex(universe: readonly BimComponent[]): QuantityIndex {
  const objects = mergeQuantityObjects(universe);
  const bySceneId = new Map<string, QuantityObject>();
  for (const object of objects) for (const id of object.componentIds) bySceneId.set(id, object);
  const select = (scope: readonly BimComponent[]): QuantityObject[] => {
    const selected = new Set<QuantityObject>();
    for (const component of scope) {
      const object = bySceneId.get(component.id);
      if (object) selected.add(object);
    }
    // Keep full-model order even when the selection arrives in a different order.
    if (selected.size === objects.length) return objects;
    return [...selected].sort((left, right) => left.ordinal - right.ordinal);
  };
  return {
    buildRows: (scope, mode, language = 'zh') => rowsForObjects(select(scope), mode, language),
    summarize: (scope, language = 'zh') => row('total', labels(language).total, select(scope), '', ''),
  };
}

export function buildQuantityRows(components: readonly BimComponent[], mode: QuantityMode, language: QuantityLanguage = 'zh', universe: readonly BimComponent[] = components): QuantityRow[] {
  if (!components.length) return [];
  return createQuantityIndex(universe).buildRows(components, mode, language);
}

export function summarizeQuantities(components: readonly BimComponent[], language: QuantityLanguage = 'zh', universe: readonly BimComponent[] = components): QuantityRow {
  if (!components.length) return row('total', labels(language).total, [], '', '');
  return createQuantityIndex(universe).summarize(components, language);
}

export interface QuantityCsvContext {
  language: QuantityLanguage;
  modelName: string;
  scope: string;
  mode?: QuantityMode;
}

function csvCell(value: string | number | null): string {
  if (value === null) return '""';
  let text = String(value);
  // Quoting alone does not stop spreadsheet programs evaluating user-controlled formulas.
  if (typeof value === 'string' && (/^[\s\u0000-\u001f]*[=+\-@]/u.test(text) || /^[\t\r\n]/u.test(text))) text = `'${text}`;
  return `"${text.replaceAll('"', '""')}"`;
}

/** UTF-8 BOM belongs in the returned text, so browser Blob downloads and direct writes agree. */
export function quantityCsv(rows: readonly QuantityRow[], context: QuantityCsvContext): string {
  const english = context.language === 'en';
  const modeNames: Record<QuantityMode, string> = english
    ? { component: 'Component', category: 'Category', level: 'Level' }
    : { component: '构件', category: '类别', level: '楼层' };
  const metadata = english
    ? [['Model', context.modelName], ['Scope', context.scope], ['Source', 'Revit instance built-in parameters; no regional deduction rules'], ['Grouping', modeNames[context.mode ?? 'component']]]
    : [['模型', context.modelName], ['范围', context.scope], ['来源', 'Revit 实例内置参数；未应用地区扣减规则'], ['分组', modeNames[context.mode ?? 'component']]];
  const head = english
    ? ['Key', 'Name', 'Category', 'Level', 'Quantity objects', 'Scene records', 'Duplicate source records', 'Component IDs', 'Source elements']
    : ['标识', '名称', '类别', '楼层', '计量对象数', '模型记录数', '重复来源记录数', '构件标识', '来源构件'];
  const names: Record<QuantityKey, string> = english ? { length: 'Length', area: 'Area', volume: 'Volume' } : { length: '长度', area: '面积', volume: '体积' };
  for (const key of QUANTITY_KEYS) {
    head.push(`${names[key]} (${QUANTITY_DEFINITIONS[key].unit})`, ...(english
      ? [`${names[key]} available`, `${names[key]} missing`, `${names[key]} invalid`, `${names[key]} unavailable`, `${names[key]} sum overflow`]
      : [`${names[key]}有效数`, `${names[key]}缺失数`, `${names[key]}无效数`, `${names[key]}读取失败数`, `${names[key]}合计溢出`]));
  }
  const lines: (string | number | null)[][] = [...metadata, [], head];
  for (const item of rows) {
    const line: (string | number | null)[] = [item.key, item.label, item.category, item.level, item.count, item.recordCount, item.duplicateSources,
      item.componentIds.join('\n'), JSON.stringify(item.sources)];
    for (const key of QUANTITY_KEYS) {
      const metric = item.metrics[key];
      line.push(metric.value, metric.available, metric.missing, metric.invalid, metric.unavailable, metric.overflow ? (english ? 'Yes' : '是') : '');
    }
    lines.push(line);
  }
  return '\ufeff' + lines.map(line => line.map(csvCell).join(',')).join('\r\n') + '\r\n';
}
