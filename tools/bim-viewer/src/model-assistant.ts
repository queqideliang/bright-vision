import type { BimComponent, BimModel, PropertyGroups } from './model.ts';
import { createQuantityIndex, QUANTITY_KEYS, type QuantityIndex } from './quantities.ts';

export type AssistantLanguage = 'zh' | 'en';
export type AssistantScope = 'filtered' | 'all' | 'selection';
export type AssistantIntent = 'summary' | 'categories' | 'levels' | 'properties' | 'missing' | 'quantities' | 'find' | 'missing-property';
export interface AssistantContext {
  model: BimModel | null;
  filtered: readonly BimComponent[];
  selected: readonly BimComponent[];
  filtering: boolean;
  filterDescription: string;
  /** Stable raw filter values, independent of translated display labels. */
  filterKey?: string;
}
export interface AssistantQuery { intent: AssistantIntent; argument?: string }
export interface AssistantEvidence {
  label: string;
  detail: string;
  /** Only these scene IDs may be located; long lists are explicitly capped. */
  componentIds: string[];
  totalRecords: number;
}
export interface AssistantReply {
  status: 'ok' | 'unsupported' | 'no-model' | 'busy' | 'empty' | 'invalid-input';
  intent?: AssistantIntent;
  scope: AssistantScope;
  recordCount: number;
  title: string;
  summary: string;
  rows: AssistantEvidence[];
  notes: string[];
}
export const ASSISTANT_LIMITS = Object.freeze({ question: 256, argument: 128, rows: 20, properties: 40, sourceIds: 100, text: 160 });

/** All app-authored visible text is centralized here. Imported names and values are never translated. */
export const assistantEnglishMessages: Readonly<Record<string, string>> = Object.freeze({
  '模型助手': 'Model assistant', '本地查询 · 不上传模型': 'Local queries · No model upload',
  '此助手使用有限的本地查询规则，不是大语言模型。可查询概览、类别、楼层、所选属性、缺失项和原始量。': 'This assistant uses a limited set of local query rules, not a large language model. Query summaries, categories, levels, selected properties, missing data and raw quantities.',
  '查询范围': 'Query scope', '当前筛选': 'Current filter', '全部模型': 'Entire model', '当前选择': 'Current selection',
  '输入模型查询': 'Enter a model query', '例如：类别数量；查找：墙；缺少参数：防火等级': 'Examples: category counts; find: wall; missing property: Fire Rating',
  '查询': 'Query', '模型概览': 'Model summary', '类别数量': 'Category counts', '楼层分布': 'Level distribution',
  '所选属性': 'Selected properties', '缺失项检查': 'Missing data', '原始量汇总': 'Raw quantity summary',
  '支持的查询': 'Supported queries',
  '支持：模型概览、类别数量、楼层分布、所选属性、缺失项检查、原始量汇总；查找：关键词；缺少参数：参数名。': 'Supported: model summary, category counts, level distribution, selected properties, missing data, raw quantity summary; find: keyword; missing property: parameter name.',
  '请打开模型后查询。': 'Open a model before querying.', '正在更新筛选结果，请稍候再查询。': 'Filter results are updating. Please wait before querying.',
  '当前范围没有构件。': 'There are no components in this scope.', '请先在模型或构件列表中选择构件。': 'Select components in the model or component list first.',
  '查询过长或包含无效控制字符。': 'The query is too long or contains invalid control characters.',
  '无法识别此查询。请使用下方列出的查询或快捷按钮。': 'This query is not supported. Use one of the listed queries or shortcut buttons.',
  '本地规则查询，不使用大模型；不判断工程合规或计算地区扣减。': 'Local rule query, without a large language model. It does not judge compliance or calculate regional deductions.',
  '依据：模型包中的构件记录及属性。记录数不等于去重后的 Revit 计量对象数。': 'Source: component records and properties in the model package. Record counts are not deduplicated Revit quantity object counts.',
  '范围：{0} · 模型：{1}': 'Scope: {0} · Model: {1}',
  '共 {0} 条模型记录。': '{0} model records.',
  '共 {0} 条模型记录，{1} 个已分类类别，{2} 个已赋值楼层。': '{0} model records, {1} assigned categories and {2} assigned levels.',
  '{0} 条模型记录': '{0} model records', '未分类': 'Unclassified', '未指定楼层': 'Unassigned level',
  '按模型记录统计类别，未合并重复来源。': 'Categories are counted by model record, without merging repeated source elements.',
  '按模型记录统计楼层；同一记录属于多个楼层时，会在各楼层分别出现，不能相加为模型总数。': 'Levels are counted by model record. A record assigned to multiple levels appears in each level; level counts must not be summed into a model total.',
  '只显示前 {0} 行，另有 {1} 行未显示；统计覆盖整个查询范围。': 'Showing the first {0} rows; {1} additional rows are not displayed. Statistics cover the entire query scope.',
  '每条依据最多定位前 {0} 条记录，完整记录数显示在依据中。': 'Each evidence item can locate at most its first {0} records. Its complete record count is shown.',
  '定位构件': 'Locate components', '定位前 {0} / {1} 条记录': 'Locate first {0} / {1} records', '定位 {0} 条记录': 'Locate {0} records',
  '类别：{0}；族：{1}；类型：{2}；楼层：{3}': 'Category: {0}; Family: {1}; Type: {2}; Levels: {3}',
  '未提供': 'Not provided', '实例属性': 'Instance properties', '类型属性': 'Type properties', '族属性': 'Family properties',
  '所选 {0} 条模型记录的标识与属性；最多显示 {1} 行。': 'Identifiers and properties of {0} selected model records; at most {1} rows are displayed.',
  '部分所选构件或属性未显示。请减少选择后查询。': 'Some selected components or properties are not displayed. Select fewer components and query again.',
  '复杂值（未展开）': 'Structured value (not expanded)', '无效数值': 'Invalid number', '空值': 'Empty value',
  '缺少类别': 'Missing category', '缺少楼层': 'Missing level', '缺少族': 'Missing family', '缺少类型': 'Missing type',
  '仅检查类别、楼层、族、类型及原始量快照状态；不等于完整模型质量审查。': 'Checks only category, level, family, type and raw quantity snapshot status; this is not a complete model quality review.',
  '未发现这些字段的缺失记录。': 'No missing records were found for these fields.',
  '长度': 'Length', '面积': 'Area', '体积': 'Volume',
  '{0}：缺失 {1}，无效 {2}，读取失败 {3}': '{0}: missing {1}, invalid {2}, read failed {3}',
  '{0}：{1} {2}；有效 {3}，缺失 {4}，无效 {5}，读取失败 {6}': '{0}: {1} {2}; available {3}, missing {4}, invalid {5}, read failed {6}',
  '{0} 个计量对象，{1} 条来源记录；合并 {2} 条重复来源记录。': '{0} quantity objects, {1} source records; {2} duplicate source records merged.',
  '仅汇总已导出的 Revit 原始实例参数；不从网格或文字属性推算。缺失、无效、读取失败不按零计算。': 'Only exported Revit instance parameters are summed. Values are not inferred from meshes or textual properties. Missing, invalid and unreadable values are not treated as zero.',
  '计量按全模型来源去重；筛选或选择涉及某来源时保留该来源的完整记录、冲突与整体量，不按楼层或可见几何拆分。': 'Quantities are deduplicated using full-model source identities. A source touched by the filter or selection retains all its records, conflicts and whole-source quantities; quantities are not split by level or visible geometry.',
  '合计超出数值范围；未给出合计值。': 'The sum exceeds the numeric range; no total is reported.',
  '无可用值': 'No available value',
  '查找结果': 'Find results', '按名称、ID、类别、族、类型进行不区分大小写的文字包含匹配，不解释自然语言条件。': 'Case-insensitive substring matching of names, IDs, categories, families and types. Natural language conditions are not interpreted.',
  '关键词“{0}”匹配 {1} 条模型记录。': 'The keyword “{0}” matches {1} model records.',
  '缺少参数检查': 'Missing property check', '参数“{0}”在 {1} 条模型记录中缺失或为空。': 'The property “{0}” is missing or empty in {1} model records.',
  '按实例、类型和族属性中的完整参数名匹配，忽略大小写；不存在或仅有空值时记为缺失。不验证值是否符合业务规范。': 'Matches complete property names across instance, type and family properties, ignoring case. Absent properties and properties with only empty values are missing. Values are not checked against business requirements.',
  '模型已切换，旧查询与定位依据已清除。': 'The model changed. Previous queries and location evidence were cleared.',
  '当前模型或范围已变化，请重新查询。': 'The current model or scope changed. Run the query again.',
  '查询未完成，请缩小范围后重试。': 'The query could not be completed. Narrow the scope and try again.',
  '模型名称未提供': 'Model name not provided',
});

export function assistantText(language: AssistantLanguage, source: string, parameters: Readonly<Record<string, string | number>> = {}): string {
  const pattern = language === 'en' ? assistantEnglishMessages[source] ?? source : source;
  return pattern.replace(/\{([^{}]+)\}/g, (token, key: string) => parameters[key] === undefined ? token : String(parameters[key]));
}

const ALIASES: Readonly<Record<Exclude<AssistantIntent, 'find' | 'missing-property'>, readonly string[]>> = {
  summary: ['模型概览', '概览', '模型摘要', 'model summary', 'summary'],
  categories: ['类别数量', '类别统计', 'category counts', 'categories'],
  levels: ['楼层分布', '楼层统计', 'level distribution', 'levels'],
  properties: ['所选属性', '选中构件属性', 'selected properties', 'selection properties'],
  missing: ['缺失项检查', '缺失项', 'missing data'],
  quantities: ['原始量汇总', '原始量', 'raw quantity summary', 'raw quantities'],
};

/** Deliberately a small command grammar. A keyword inside a longer prompt is not enough to execute a query. */
export function parseAssistantQuery(question: string): AssistantQuery | null {
  if (question.length > ASSISTANT_LIMITS.question || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(question)) return null;
  const value = question.trim().replace(/[。？?！!]+$/u, '').trim().toLocaleLowerCase();
  for (const [intent, aliases] of Object.entries(ALIASES)) {
    if (aliases.includes(value)) return { intent: intent as AssistantQuery['intent'] };
  }
  const command = /^(查找|find|缺少参数|missing property)\s*[:：]\s*(.+)$/iu.exec(question.trim());
  if (!command) return null;
  const argument = command[2]!.trim();
  if (!argument || argument.length > ASSISTANT_LIMITS.argument || /[\r\n]/u.test(argument)) return null;
  return { intent: /^(查找|find)$/iu.test(command[1]!) ? 'find' : 'missing-property', argument };
}

const names: Readonly<Record<AssistantIntent, string>> = {
  summary: '模型概览', categories: '类别数量', levels: '楼层分布', properties: '所选属性', missing: '缺失项检查',
  quantities: '原始量汇总', find: '查找结果', 'missing-property': '缺少参数检查',
};
const metricNames = { length: '长度', area: '面积', volume: '体积' } as const;
const units = { length: 'm', area: 'm²', volume: 'm³' } as const;
const shorten = (value: string) => value.length <= ASSISTANT_LIMITS.text ? value : value.slice(0, ASSISTANT_LIMITS.text - 1) + '…';
const empty = (value: unknown) => value === null || value === undefined || (typeof value === 'string' && value.trim() === '');
const groupsOf = (component: BimComponent): PropertyGroups[] => [component.properties, component.typeProperties, component.familyProperties];

function hasProperty(component: BimComponent, name: string): boolean {
  const normalized = name.toLocaleLowerCase();
  for (const properties of groupsOf(component)) {
    for (const group of Object.values(properties)) {
      for (const [key, value] of Object.entries(group)) if (key.toLocaleLowerCase() === normalized && !empty(value)) return true;
    }
  }
  return false;
}

function sourceRow(label: string, detail: string, components: readonly BimComponent[]): AssistantEvidence {
  return { label: shorten(label), detail, componentIds: components.slice(0, ASSISTANT_LIMITS.sourceIds).map(component => component.id), totalRecords: components.length };
}

/** Local-only engine; no network, credentials, prompts, dynamic code, or geometry-derived quantities. */
export class ModelAssistant {
  private model: BimModel | null = null;
  private components = new Map<string, BimComponent>();
  private quantityIndex: QuantityIndex | null = null;

  reset(): void {
    this.model = null; this.components.clear(); this.quantityIndex = null;
  }

  query(question: string, context: AssistantContext, requestedScope: AssistantScope = 'filtered', language: AssistantLanguage = 'zh'): AssistantReply {
    const t = (source: string, parameters?: Readonly<Record<string, string | number>>) => assistantText(language, source, parameters);
    const parsed = parseAssistantQuery(question);
    const scope = parsed?.intent === 'properties' ? 'selection' : requestedScope;
    const reply: AssistantReply = { status: 'ok', scope, recordCount: 0, title: t(parsed ? names[parsed.intent] : '支持的查询'), summary: '', rows: [], notes: [t('本地规则查询，不使用大模型；不判断工程合规或计算地区扣减。')] };
    if (!context.model) { this.reset(); reply.status = 'no-model'; reply.summary = t('请打开模型后查询。'); return reply; }
    if (context.model !== this.model) {
      this.model = context.model;
      this.components = new Map(context.model.components.map(component => [component.id, component]));
      this.quantityIndex = null;
    }
    if (context.filtering && scope === 'filtered') { reply.status = 'busy'; reply.summary = t('正在更新筛选结果，请稍候再查询。'); return reply; }
    if (!parsed) {
      reply.status = question.length > ASSISTANT_LIMITS.question || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(question) ? 'invalid-input' : 'unsupported';
      reply.summary = t(reply.status === 'invalid-input' ? '查询过长或包含无效控制字符。' : '无法识别此查询。请使用下方列出的查询或快捷按钮。');
      reply.notes.push(t('支持：模型概览、类别数量、楼层分布、所选属性、缺失项检查、原始量汇总；查找：关键词；缺少参数：参数名。'));
      return reply;
    }
    reply.intent = parsed.intent;
    const source = scope === 'all' ? context.model.components : scope === 'selection' ? context.selected : context.filtered;
    const ids = new Set<string>();
    const components: BimComponent[] = [];
    for (const candidate of source) {
      const canonical = this.components.get(candidate.id);
      if (canonical && !ids.has(canonical.id)) { components.push(canonical); ids.add(canonical.id); }
    }
    reply.recordCount = components.length;
    const scopeName = t(scope === 'all' ? '全部模型' : scope === 'selection' ? '当前选择' : '当前筛选');
    reply.notes.push(t('范围：{0} · 模型：{1}', { '0': scopeName, '1': shorten(context.model.name) || t('模型名称未提供') }));
    if (scope === 'filtered' && context.filterDescription) reply.notes.push(shorten(context.filterDescription));
    if (!components.length) {
      reply.status = 'empty'; reply.summary = t(scope === 'selection' ? '请先在模型或构件列表中选择构件。' : '当前范围没有构件。'); return reply;
    }
    const count = (values: readonly BimComponent[]) => t('{0} 条模型记录', { '0': values.length });
    const generalSource = () => reply.notes.push(t('依据：模型包中的构件记录及属性。记录数不等于去重后的 Revit 计量对象数。'));
    let omittedRows = 0;
    if (parsed.intent === 'summary') {
      const categories = new Set(components.map(component => component.category).filter(value => value.trim()));
      const levels = new Set(components.flatMap(component => component.levelNames).filter(value => value.trim()));
      reply.summary = t('共 {0} 条模型记录，{1} 个已分类类别，{2} 个已赋值楼层。', { '0': components.length, '1': categories.size, '2': levels.size });
      reply.rows.push(sourceRow(t('模型概览'), count(components), components)); generalSource();
    } else if (parsed.intent === 'categories' || parsed.intent === 'levels') {
      const grouped = new Map<string, BimComponent[]>();
      for (const component of components) {
        const values = parsed.intent === 'categories' ? [component.category] : [...new Set(component.levelNames.filter(value => value.trim()))];
        if (!values.length) values.push('');
        for (const value of values) {
          const group = grouped.get(value);
          if (group) group.push(component); else grouped.set(value, [component]);
        }
      }
      const orderedGroups = [...grouped].sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]));
      omittedRows = Math.max(0, orderedGroups.length - ASSISTANT_LIMITS.rows);
      for (const [label, values] of orderedGroups.slice(0, ASSISTANT_LIMITS.rows)) {
        reply.rows.push(sourceRow(label || t(parsed.intent === 'categories' ? '未分类' : '未指定楼层'), count(values), values));
      }
      reply.summary = t('共 {0} 条模型记录。', { '0': components.length });
      reply.notes.push(t(parsed.intent === 'categories' ? '按模型记录统计类别，未合并重复来源。' : '按模型记录统计楼层；同一记录属于多个楼层时，会在各楼层分别出现，不能相加为模型总数。')); generalSource();
    } else if (parsed.intent === 'properties') {
      let truncated = false;
      let propertyRows = 0;
      const display = (value: unknown): string => empty(value) ? t('空值') : typeof value === 'number' && !Number.isFinite(value) ? t('无效数值') : typeof value === 'object' ? t('复杂值（未展开）') : shorten(String(value));
      outer: for (const component of components) {
        if (propertyRows >= ASSISTANT_LIMITS.properties) { truncated = true; break; }
        reply.rows.push(sourceRow(component.name || component.id, `${shorten(component.id)} · ${t('类别：{0}；族：{1}；类型：{2}；楼层：{3}', {
          '0': shorten(component.category) || t('未提供'), '1': shorten(component.family) || t('未提供'), '2': shorten(component.type) || t('未提供'), '3': shorten(component.levelNames.join(', ')) || t('未提供'),
        })}`, [component])); propertyRows++;
        for (const [index, properties] of groupsOf(component).entries()) {
          for (const [group, values] of Object.entries(properties)) {
            for (const [key, value] of Object.entries(values)) {
              if (propertyRows >= ASSISTANT_LIMITS.properties) { truncated = true; break outer; }
              reply.rows.push(sourceRow(`${t(['实例属性', '类型属性', '族属性'][index]!)} / ${shorten(group)} / ${shorten(key)}`, display(value), [component])); propertyRows++;
            }
          }
        }
      }
      reply.summary = t('所选 {0} 条模型记录的标识与属性；最多显示 {1} 行。', { '0': components.length, '1': ASSISTANT_LIMITS.properties });
      if (truncated) reply.notes.push(t('部分所选构件或属性未显示。请减少选择后查询。')); generalSource();
    } else if (parsed.intent === 'missing') {
      const tests: [string, (component: BimComponent) => boolean][] = [
        ['缺少类别', component => !component.category.trim()], ['缺少楼层', component => !component.levelNames.some(level => level.trim())],
        ['缺少族', component => !component.family.trim()], ['缺少类型', component => !component.type.trim()],
      ];
      for (const [label, isMissing] of tests) {
        const values = components.filter(isMissing);
        if (values.length) reply.rows.push(sourceRow(t(label), count(values), values));
      }
      for (const key of QUANTITY_KEYS) {
        const values = components.filter(component => !component.quantitySnapshot || component.quantitySnapshot.metrics[key].status !== 'available');
        if (!values.length) continue;
        const states = { missing: 0, invalid: 0, unavailable: 0 };
        for (const component of values) {
          const status = component.quantitySnapshot?.metrics[key].status ?? (component.quantityIssue ? 'invalid' : 'missing');
          if (status !== 'available') states[status]++;
        }
        reply.rows.push(sourceRow(t(metricNames[key]), t('{0}：缺失 {1}，无效 {2}，读取失败 {3}', { '0': t(metricNames[key]), '1': states.missing, '2': states.invalid, '3': states.unavailable }), values));
      }
      reply.summary = reply.rows.length ? t('共 {0} 条模型记录。', { '0': components.length }) : t('未发现这些字段的缺失记录。');
      reply.notes.push(t('仅检查类别、楼层、族、类型及原始量快照状态；不等于完整模型质量审查。')); generalSource();
    } else if (parsed.intent === 'quantities') {
      this.quantityIndex ??= createQuantityIndex(context.model.components);
      const total = this.quantityIndex.summarize(components, language);
      reply.summary = t('{0} 个计量对象，{1} 条来源记录；合并 {2} 条重复来源记录。', { '0': total.count, '1': total.recordCount, '2': total.duplicateSources });
      for (const key of QUANTITY_KEYS) {
        const metric = total.metrics[key];
        const value = metric.value === null ? t('无可用值') : metric.value.toLocaleString(language === 'en' ? 'en-US' : 'zh-CN', { maximumFractionDigits: 6 });
        reply.rows.push({ label: `${t(metricNames[key])} (${units[key]})`, detail: t('{0}：{1} {2}；有效 {3}，缺失 {4}，无效 {5}，读取失败 {6}', {
          '0': t(metricNames[key]), '1': value, '2': metric.value === null ? '' : units[key], '3': metric.available, '4': metric.missing, '5': metric.invalid, '6': metric.unavailable,
        }), componentIds: total.componentIds.slice(0, ASSISTANT_LIMITS.sourceIds), totalRecords: total.recordCount });
        if (metric.overflow) reply.notes.push(t('合计超出数值范围；未给出合计值。'));
      }
      reply.notes.push(t('仅汇总已导出的 Revit 原始实例参数；不从网格或文字属性推算。缺失、无效、读取失败不按零计算。'), t('计量按全模型来源去重；筛选或选择涉及某来源时保留该来源的完整记录、冲突与整体量，不按楼层或可见几何拆分。'));
    } else if (parsed.intent === 'find') {
      const argument = parsed.argument!.toLocaleLowerCase();
      const found = components.filter(component => [component.id, component.name, component.category, component.family, component.type].some(value => value.toLocaleLowerCase().includes(argument)));
      omittedRows = Math.max(0, found.length - ASSISTANT_LIMITS.rows);
      for (const component of found.slice(0, ASSISTANT_LIMITS.rows)) reply.rows.push(sourceRow(component.name || component.id, `${shorten(component.id)} · ${shorten(component.category)}`, [component]));
      reply.summary = t('关键词“{0}”匹配 {1} 条模型记录。', { '0': shorten(parsed.argument!), '1': found.length });
      reply.notes.push(t('按名称、ID、类别、族、类型进行不区分大小写的文字包含匹配，不解释自然语言条件。')); generalSource();
    } else {
      const values = components.filter(component => !hasProperty(component, parsed.argument!));
      reply.summary = t('参数“{0}”在 {1} 条模型记录中缺失或为空。', { '0': shorten(parsed.argument!), '1': values.length });
      if (values.length) reply.rows.push(sourceRow(parsed.argument!, count(values), values));
      reply.notes.push(t('按实例、类型和族属性中的完整参数名匹配，忽略大小写；不存在或仅有空值时记为缺失。不验证值是否符合业务规范。')); generalSource();
    }
    const rowLimit = parsed.intent === 'properties' ? ASSISTANT_LIMITS.properties : ASSISTANT_LIMITS.rows;
    if (reply.rows.length > rowLimit || omittedRows) {
      reply.notes.push(t('只显示前 {0} 行，另有 {1} 行未显示；统计覆盖整个查询范围。', { '0': rowLimit, '1': omittedRows + Math.max(0, reply.rows.length - rowLimit) }));
      reply.rows.length = rowLimit;
    }
    if (reply.rows.some(row => row.totalRecords > row.componentIds.length)) reply.notes.push(t('每条依据最多定位前 {0} 条记录，完整记录数显示在依据中。', { '0': ASSISTANT_LIMITS.sourceIds }));
    return reply;
  }
}
