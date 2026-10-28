import type { BimComponent } from './model.ts';
import { createQuantityIndex, quantityCsv, QUANTITY_KEYS } from './quantities.ts';
import type { QuantityIndex, QuantityRow, QuantityMode } from './quantities.ts';
import { getLanguage, locale, tr } from './i18n.ts';
import './quantity-ui.css';

export interface QuantityContext {
  modelName: string;
  all: readonly BimComponent[];
  filtered: readonly BimComponent[];
  selected: readonly BimComponent[];
  filterDescription: string;
  filtering: boolean;
}

const PAGE_SIZE = 50;
const labels = { length: '长度', area: '面积', volume: '体积' } as const;
const units = { length: 'm', area: 'm²', volume: 'm³' } as const;

function text(tag: string, value: string, className?: string): HTMLElement {
  const node = document.createElement(tag);
  node.textContent = value;
  if (className) node.className = className;
  return node;
}

/** The table uses source parameters only. Render at most one page of DOM rows. */
export class QuantityPanel {
  private readonly root: HTMLElement;
  private readonly getContext: () => QuantityContext;
  private readonly focus: (ids: readonly string[]) => void;
  private readonly notify: (message: string) => void;
  private readonly scope: HTMLSelectElement;
  private readonly mode: HTMLSelectElement;
  private readonly exportButton: HTMLButtonElement;
  private readonly previous: HTMLButtonElement;
  private readonly next: HTMLButtonElement;
  private rows: QuantityRow[] = [];
  private summary: QuantityRow | null = null;
  private page = 0;
  private cachedComponents: readonly BimComponent[] | null = null;
  private cachedMode = '';
  private cachedLanguage = '';
  private cachedModel: readonly BimComponent[] | null = null;
  private index: QuantityIndex | null = null;

  constructor(root: HTMLElement, getContext: () => QuantityContext,
    focus: (ids: readonly string[]) => void, notify: (message: string) => void) {
    this.root = root;
    this.getContext = getContext;
    this.focus = focus;
    this.notify = notify;
    root.innerHTML = `
      <div class="quantity-filters">
        <label class="tool-field"><span id="quantity-scope-label"></span><select id="quantity-scope"><option value="filtered"></option><option value="selection"></option></select></label>
        <label class="tool-field"><span id="quantity-mode-label"></span><select id="quantity-mode"><option value="component"></option><option value="category"></option><option value="level"></option></select></label>
        <button id="quantity-export" class="primary" type="button"></button>
      </div>
      <p id="quantity-range" class="tool-note"></p>
      <div id="quantity-totals" class="quantity-totals" aria-live="polite"></div>
      <p id="quantity-coverage" class="tool-note"></p>
      <p id="quantity-missing" class="quantity-message" hidden></p>
      <div class="quantity-table-scroll" tabindex="0"><table class="quantity-table"><thead id="quantity-head"></thead><tbody id="quantity-body"></tbody></table></div>
      <div class="quantity-pages"><button id="quantity-previous" class="quiet" type="button"></button><span id="quantity-page" role="status"></span><button id="quantity-next" class="quiet" type="button"></button></div>
      <p id="quantity-source" class="tool-note"></p>`;
    this.scope = this.find('quantity-scope');
    this.mode = this.find('quantity-mode');
    this.exportButton = this.find('quantity-export');
    this.previous = this.find('quantity-previous');
    this.next = this.find('quantity-next');
    this.scope.onchange = this.mode.onchange = () => { this.page = 0; this.refresh(); };
    this.previous.onclick = () => { this.page--; this.renderRows(); };
    this.next.onclick = () => { this.page++; this.renderRows(); };
    this.exportButton.onclick = () => this.download();
  }

  private find<T extends HTMLElement = HTMLElement>(id: string): T {
    return this.root.querySelector<T>(`#${id}`)!;
  }

  private selectedComponents(context: QuantityContext): readonly BimComponent[] {
    return this.scope.value === 'selection' ? context.selected : context.filtered;
  }

  private scopeLabel(context: QuantityContext): string {
    const range = this.scope.value === 'selection' ? tr('当前选择') : context.filterDescription;
    return `${range} · ${tr('涉及来源的整体量，未按楼层拆分')}`;
  }

  refresh(): void {
    const context = this.getContext();
    const components = this.selectedComponents(context);
    const language = getLanguage();
    const mode = this.mode.value as QuantityMode;
    if (context.all !== this.cachedModel) {
      this.index = createQuantityIndex(context.all);
      this.cachedModel = context.all;
      this.cachedComponents = null;
    }
    if (components !== this.cachedComponents || mode !== this.cachedMode || language !== this.cachedLanguage) {
      this.rows = this.index!.buildRows(components, mode, language);
      this.summary = this.index!.summarize(components, language);
      this.cachedComponents = components;
      this.cachedMode = mode;
      this.cachedLanguage = language;
      this.page = 0;
    }
    this.find('quantity-scope-label').textContent = tr('量表范围');
    this.find('quantity-mode-label').textContent = tr('分组方式');
    this.scope.options[0]!.textContent = tr('当前筛选');
    this.scope.options[1]!.textContent = tr('当前选择');
    this.mode.options[0]!.textContent = tr('按构件');
    this.mode.options[1]!.textContent = tr('按类别');
    this.mode.options[2]!.textContent = tr('按楼层');
    this.exportButton.textContent = tr('导出 CSV');
    this.previous.textContent = tr('上一页');
    this.next.textContent = tr('下一页');
    this.root.setAttribute('aria-busy', String(context.filtering));
    this.exportButton.disabled = !this.rows.length || context.filtering;
    this.find('quantity-range').textContent = context.filtering ? tr('正在更新筛选结果，请稍候…')
      : tr('范围：{0}。显示操作不裁剪计量几何；“当前选择”随选中对象变化。', { '0': this.scopeLabel(context) });
    this.find('quantity-source').textContent = tr('来源：Revit 实例参数，采用其原始计量口径；本表不计算地区规则扣减。点击名称定位构件。');
    const summary = this.summary!;
    this.find('quantity-coverage').textContent = tr('{0} 个计量对象 · {1} 条模型记录 · 合并 {2} 条重复来源记录', {
      '0': summary.count.toLocaleString(locale()), '1': summary.recordCount.toLocaleString(locale()),
      '2': summary.duplicateSources.toLocaleString(locale()),
    });
    const totals = this.find('quantity-totals');
    totals.replaceChildren();
    for (const key of QUANTITY_KEYS) {
      const metric = summary.metrics[key];
      const block = text('div', '', 'quantity-total');
      block.dataset.metric = key;
      block.append(text('span', `${tr(labels[key])} (${units[key]})`),
        text('strong', this.value(metric.value), 'quantity-total-value'),
        text('small', tr('有效 {0} / {1}', { '0': metric.available, '1': summary.count })));
      block.title = this.metricStatus(metric);
      totals.append(block);
    }
    const notice = this.find('quantity-missing');
    const noSnapshots = components.length > 0 && components.every(component => !component.quantitySnapshot && !component.quantityIssue);
    const anyMissing = QUANTITY_KEYS.some(key => summary.metrics[key].available < summary.count || summary.metrics[key].overflow);
    notice.hidden = !components.length || !anyMissing;
    notice.textContent = noSnapshots
      ? tr('此模型包没有计量快照，当前仅统计对象数。请用 BV 1.4.0 或更新插件重新导出；缺失量不会按零计算。')
      : tr('部分指标缺失、无效或不可读取，合计只包含有效值。将鼠标停在数值上可查看覆盖情况。');
    this.renderRows();
  }

  private value(value: number | null): string {
    return value === null ? '—' : value.toLocaleString(locale(), { maximumFractionDigits: 6 });
  }

  private metricStatus(metric: QuantityRow['metrics']['length']): string {
    if (metric.overflow) return tr('数值合计超出可表示范围，请缩小筛选范围。');
    return tr('有效 {0} · 缺失 {1} · 无效 {2} · 读取失败 {3}', {
      '0': metric.available, '1': metric.missing, '2': metric.invalid, '3': metric.unavailable,
    });
  }

  private renderRows(): void {
    const pages = Math.max(1, Math.ceil(this.rows.length / PAGE_SIZE));
    this.page = Math.max(0, Math.min(this.page, pages - 1));
    const header = document.createElement('tr');
    for (const label of ['构件 / 分组', '类别', '楼层', '计量对象']) {
      const th = text('th', tr(label)); th.setAttribute('scope', 'col'); header.append(th);
    }
    for (const key of QUANTITY_KEYS) {
      const th = text('th', `${tr(labels[key])} (${units[key]})`); th.setAttribute('scope', 'col'); header.append(th);
    }
    this.find('quantity-head').replaceChildren(header);
    const body = this.find('quantity-body');
    body.replaceChildren();
    for (const row of this.rows.slice(this.page * PAGE_SIZE, (this.page + 1) * PAGE_SIZE)) {
      const line = document.createElement('tr');
      line.dataset.key = row.key;
      const nameCell = document.createElement('td');
      const button = text('button', row.label, 'quantity-focus') as HTMLButtonElement;
      button.type = 'button';
      button.title = tr('定位 {0} 个计量对象', { '0': row.count });
      button.onclick = () => this.focus(row.componentIds);
      nameCell.append(button);
      if (row.duplicateSources) nameCell.append(text('small', tr('已合并 {0} 条重复记录', { '0': row.duplicateSources }), 'quantity-duplicate'));
      line.append(nameCell, text('td', row.category), text('td', row.level), text('td', row.count.toLocaleString(locale())));
      for (const key of QUANTITY_KEYS) {
        const cell = text('td', this.value(row.metrics[key].value), 'quantity-number');
        cell.dataset.metric = key;
        cell.title = this.metricStatus(row.metrics[key]);
        line.append(cell);
      }
      body.append(line);
    }
    if (!this.rows.length) {
      const row = document.createElement('tr');
      const empty = text('td', tr(this.scope.value === 'selection' ? '请先选择要统计的构件。' : '当前筛选没有构件。')) as HTMLTableCellElement;
      empty.colSpan = 7; empty.className = 'quantity-empty'; row.append(empty); body.append(row);
    }
    this.previous.disabled = this.page === 0;
    this.next.disabled = this.page >= pages - 1;
    this.find('quantity-page').textContent = tr('第 {0} / {1} 页 · 共 {2} 行', {
      '0': this.page + 1, '1': pages, '2': this.rows.length.toLocaleString(locale()),
    });
  }

  private download(): void {
    const context = this.getContext();
    if (context.filtering) return;
    this.refresh();
    if (!this.rows.length) return;
    try {
      const csv = quantityCsv(this.rows, { language: getLanguage(), modelName: context.modelName,
        scope: this.scopeLabel(context), mode: this.mode.value as QuantityMode });
      const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
      const link = document.createElement('a');
      const name = context.modelName.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').slice(0, 100) || 'model';
      link.download = `${name}-quantities.csv`; link.href = url; document.body.append(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      this.notify(tr('已导出当前范围的量表 CSV。'));
    } catch {
      this.notify(tr('量表导出未完成，请缩小筛选范围后重试。'));
    }
  }

  clear(): void {
    this.rows = []; this.summary = null; this.cachedComponents = null; this.cachedModel = null; this.index = null; this.page = 0;
    for (const id of ['quantity-body', 'quantity-totals']) this.find(id).replaceChildren();
  }
}
