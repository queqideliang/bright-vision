import {
  ModelAssistant, ASSISTANT_LIMITS, assistantText,
  type AssistantContext, type AssistantLanguage, type AssistantReply, type AssistantScope,
} from './model-assistant.ts';
import type { BimModel } from './model.ts';
import './assistant-ui.css';

export interface AssistantPanelOptions {
  getContext: () => AssistantContext;
  onLocate: (ids: readonly string[]) => void;
  getLanguage: () => AssistantLanguage;
}

interface ContextStamp {
  model: BimModel | null;
  filtered: AssistantContext['filtered'];
  filteredIds: string[];
  selected: string[];
  filtering: boolean;
  filterKey: string;
}

function element<K extends keyof HTMLElementTagNameMap>(tag: K, id?: string, className?: string): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (id) node.id = id;
  if (className) node.className = className;
  return node;
}

/** No HTML from an archive is interpreted; names, properties and query text enter via textContent/value. */
export class AssistantPanel {
  private readonly root: HTMLElement;
  private readonly options: AssistantPanelOptions;
  private readonly engine = new ModelAssistant();
  private readonly events = new AbortController();
  private resultEvents = new AbortController();
  private readonly status = element('p', 'assistant-status', 'assistant-status');
  private readonly intro = element('p', undefined, 'tool-note');
  private readonly scopeLabel = element('label', undefined, 'tool-field');
  private readonly scopeText = element('span');
  private readonly scope = element('select', 'assistant-scope');
  private readonly questionLabel = element('label', undefined, 'assistant-question-label');
  private readonly question = element('textarea', 'assistant-question');
  private readonly submit = element('button', 'assistant-submit', 'primary');
  private readonly shortcuts = element('div', undefined, 'assistant-shortcuts');
  private readonly shortcutButtons: { button: HTMLButtonElement; source: string }[] = [];
  private readonly result = element('section', 'assistant-result', 'assistant-result');
  private readonly resultTitle = element('h3');
  private readonly summary = element('p', 'assistant-summary');
  private readonly evidence = element('ul', 'assistant-evidence');
  private readonly notes = element('ul', 'assistant-notes', 'tool-note');
  private readonly help = element('details', undefined, 'assistant-help');
  private readonly helpSummary = element('summary');
  private readonly helpText = element('p', undefined, 'tool-note');
  private lastModel: BimModel | null;
  private lastLanguage: AssistantLanguage;
  private answer: AssistantReply | null = null;
  private answerContext: ContextStamp | null = null;
  private lastQuery = '';
  private notice = '';
  private destroyed = false;

  constructor(root: HTMLElement, options: AssistantPanelOptions) {
    this.root = root; this.options = options;
    this.lastModel = options.getContext().model;
    this.lastLanguage = options.getLanguage();
    const form = element('form', 'assistant-form');
    this.scopeLabel.htmlFor = this.scope.id;
    for (const value of ['filtered', 'all', 'selection']) {
      const option = element('option'); option.value = value; this.scope.append(option);
    }
    this.scopeLabel.append(this.scopeText, this.scope);
    this.questionLabel.htmlFor = this.question.id;
    this.question.rows = 3; this.question.maxLength = ASSISTANT_LIMITS.question;
    this.question.autocomplete = 'off'; this.question.spellcheck = false;
    this.submit.type = 'submit';
    form.append(this.scopeLabel, this.questionLabel, this.question, this.submit);
    for (const [value, source] of [['summary', '模型概览'], ['categories', '类别数量'], ['levels', '楼层分布'], ['properties', '所选属性'], ['missing', '缺失项检查'], ['quantities', '原始量汇总']]) {
      const button = element('button', undefined, 'quiet'); button.type = 'button'; button.dataset.assistantQuery = value;
      button.addEventListener('click', () => { this.question.value = this.t(source!); this.runQuery(); }, { signal: this.events.signal });
      this.shortcutButtons.push({ button, source: source! }); this.shortcuts.append(button);
    }
    this.result.setAttribute('aria-live', 'polite'); this.result.setAttribute('aria-atomic', 'true');
    this.result.append(this.resultTitle, this.summary, this.evidence, this.notes);
    this.help.append(this.helpSummary, this.helpText);
    this.root.replaceChildren(this.status, this.intro, form, this.shortcuts, this.result, this.help);
    form.addEventListener('submit', event => { event.preventDefault(); this.runQuery(); }, { signal: this.events.signal });
    this.scope.addEventListener('change', () => { this.clear('当前模型或范围已变化，请重新查询。'); this.refresh(); }, { signal: this.events.signal });
    // Listen only on our textarea, with no global keyboard shortcuts or mouse capture.
    this.question.addEventListener('keydown', event => {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) { event.preventDefault(); this.runQuery(); }
    }, { signal: this.events.signal });
    window.addEventListener('bright-language-changed', () => this.refresh(), { signal: this.events.signal });
    this.refresh();
  }

  private t(source: string, parameters?: Readonly<Record<string, string | number>>): string {
    return assistantText(this.options.getLanguage(), source, parameters);
  }

  private capture(context: AssistantContext): ContextStamp {
    return { model: context.model, filtered: context.filtered, filteredIds: this.answer?.scope === 'filtered' ? context.filtered.map(component => component.id) : [],
      selected: context.selected.map(component => component.id), filtering: context.filtering, filterKey: context.filterKey ?? context.filterDescription };
  }

  private matches(context: AssistantContext): boolean {
    const previous = this.answerContext;
    if (!previous || previous.model !== context.model) return false;
    if (this.answer?.scope === 'filtered' && (previous.filtered !== context.filtered || previous.filtering !== context.filtering || previous.filterKey !== (context.filterKey ?? context.filterDescription)
      || previous.filteredIds.length !== context.filtered.length || previous.filteredIds.some((id, index) => id !== context.filtered[index]?.id))) return false;
    if (this.answer?.scope === 'selection' && (previous.selected.length !== context.selected.length || previous.selected.some((id, index) => id !== context.selected[index]?.id))) return false;
    return true;
  }

  private clear(notice = ''): void {
    this.answer = null; this.answerContext = null; this.lastQuery = ''; this.notice = notice;
    this.evidence.replaceChildren(); this.notes.replaceChildren(); this.summary.textContent = ''; this.resultTitle.textContent = '';
  }

  refresh(): void {
    if (this.destroyed) return;
    const context = this.options.getContext();
    const language = this.options.getLanguage();
    if (context.model !== this.lastModel) {
      this.engine.reset(); this.clear('模型已切换，旧查询与定位依据已清除。'); this.question.value = ''; this.lastModel = context.model;
    } else if (this.answer && !this.matches(context)) this.clear('当前模型或范围已变化，请重新查询。');
    else if (this.answer && language !== this.lastLanguage) {
      // Recompute application text; imported names and property values keep their source language.
      this.answer = this.engine.query(this.lastQuery, context, this.scope.value as AssistantScope, language);
    }
    this.lastLanguage = language;
    this.status.textContent = this.t('本地查询 · 不上传模型');
    this.intro.textContent = this.t('此助手使用有限的本地查询规则，不是大语言模型。可查询概览、类别、楼层、所选属性、缺失项和原始量。');
    this.scopeText.textContent = this.t('查询范围');
    for (const [index, label] of ['当前筛选', '全部模型', '当前选择'].entries()) this.scope.options[index]!.textContent = this.t(label);
    this.questionLabel.textContent = this.t('输入模型查询'); this.question.placeholder = this.t('例如：类别数量；查找：墙；缺少参数：防火等级');
    this.submit.textContent = this.t('查询');
    this.helpSummary.textContent = this.t('支持的查询');
    this.helpText.textContent = this.t('支持：模型概览、类别数量、楼层分布、所选属性、缺失项检查、原始量汇总；查找：关键词；缺少参数：参数名。');
    this.submit.disabled = !context.model || (this.scope.value === 'filtered' && context.filtering);
    for (const { button, source } of this.shortcutButtons) { button.textContent = this.t(source); button.disabled = !context.model || (this.scope.value === 'filtered' && context.filtering && button.dataset.assistantQuery !== 'properties'); }
    this.root.setAttribute('aria-busy', String(context.filtering && this.scope.value === 'filtered'));
    this.render();
  }

  private runQuery(): void {
    if (this.destroyed) return;
    this.refresh();
    const context = this.options.getContext();
    try {
      this.lastQuery = this.question.value;
      this.answer = this.engine.query(this.lastQuery, context, this.scope.value as AssistantScope, this.options.getLanguage());
      this.scope.value = this.answer.scope;
      this.answerContext = this.capture(context); this.notice = '';
    } catch {
      this.clear('查询未完成，请缩小范围后重试。');
    }
    this.refresh();
  }

  private render(): void {
    this.resultEvents.abort(); this.resultEvents = new AbortController();
    this.evidence.replaceChildren(); this.notes.replaceChildren();
    if (!this.answer) {
      delete this.result.dataset.status;
      this.resultTitle.textContent = '';
      this.summary.textContent = this.notice ? this.t(this.notice) : '';
      this.result.hidden = !this.notice;
      return;
    }
    this.result.hidden = false;
    this.result.dataset.status = this.answer.status;
    this.resultTitle.textContent = this.answer.title;
    this.summary.textContent = this.answer.summary;
    const modelAtAnswer = this.answerContext?.model;
    for (const row of this.answer.rows) {
      const item = element('li'); item.dataset.sourceCount = String(row.totalRecords);
      const label = element('strong'); label.textContent = row.label;
      const detail = element('p'); detail.textContent = row.detail;
      const locate = element('button', undefined, 'assistant-locate text-button'); locate.type = 'button';
      locate.textContent = this.t(row.totalRecords > row.componentIds.length ? '定位前 {0} / {1} 条记录' : '定位 {0} 条记录', { '0': row.componentIds.length, '1': row.totalRecords });
      locate.disabled = !row.componentIds.length;
      locate.addEventListener('click', () => {
        const context = this.options.getContext();
        if (this.destroyed || modelAtAnswer !== context.model || !this.matches(context)) { this.refresh(); return; }
        this.options.onLocate([...row.componentIds]);
      }, { signal: this.resultEvents.signal });
      item.append(label, detail, locate); this.evidence.append(item);
    }
    for (const note of this.answer.notes) { const item = element('li'); item.textContent = note; this.notes.append(item); }
  }

  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true; this.events.abort(); this.resultEvents.abort(); this.engine.reset(); this.clear(); this.lastModel = null; this.root.replaceChildren();
  }
}
