import './style.css';
import './navigation-ui.css';
import { MODEL_LIMITS } from './import/parse.ts';
import { brand, brandPresentation } from './brand.ts';
import { createDemoModel } from './demo.ts';
import type { BimComponent, BimModel, PropertyGroups } from './model.ts';
import { ModelViewer } from './viewer/model-viewer.ts';
import type { ArchiveChoice } from './import/packages.ts';
import type { ArchiveReference } from './desktop.d.ts';
import { UNCLASSIFIED_CATEGORY, visibleTreeRows } from './component-index.ts';
import type { IndexRecord, IndexResult, TreeGroup } from './component-index.ts';
import { compactView, expandView, modelViewKey, readSavedViews } from './saved-views.ts';
import type { SavedView } from './saved-views.ts';
import { bindStaticTranslations, getLanguage, locale, setLanguage, tr, translatedMessage, translatedProgress } from './i18n.ts';
import { mountUpdateUI } from './update-ui.ts';
import { mountRevitUI } from './revit-ui.ts';
import { QuantityPanel } from './quantity-ui.ts';
import { AssistantPanel } from './assistant-ui.ts';
const icons: Record<string, string> = {
    open: '<path d="M3 7h6l2 2h10l-3 10H3V7Z"/><path d="M3 7V4h6l2 3"/>',
    fit: '<path d="M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5"/><path d="m8 8 8 8m0-8-8 8"/>',
    eye: '<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
    hide: '<path d="m3 3 18 18M10.5 5.2A12 12 0 0 1 12 5c6 0 10 7 10 7a18 18 0 0 1-3 3.7M6.2 6.2A21 21 0 0 0 2 12s4 7 10 7c1.8 0 3.5-.6 5-1.5"/>',
    isolate: '<path d="m12 3 9 5v8l-9 5-9-5V8l9-5Z"/><path d="m3 8 9 5 9-5M12 13v8"/>',
    close: '<path d="m6 6 12 12M18 6 6 18"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    chevron: '<path d="m8 10 4 4 4-4"/>',
    cube: '<path d="m12 3 9 5v8l-9 5-9-5V8l9-5Z"/><path d="m3 8 9 5 9-5M12 13v8"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M15 8V4H4v11h4"/>',
};
const icon = (name: string) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${icons[name] ?? ''}</svg>`;
const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const app = $('app');
app.innerHTML = `
  <header class="app-header">
    <div class="brand"><span class="brand-mark" aria-hidden="true"></span><div><h1 id="brand-name"></h1><span id="brand-subtitle"></span></div></div>
    <div class="header-right"><span class="privacy"><i></i>文件在本机处理</span><button id="language-toggle" class="quiet" title="切换界面语言" aria-label="切换界面语言">English</button><button id="revit-button" class="quiet">Revit插件</button><button id="update-button" class="quiet">更新</button><button id="help-button" class="quiet">使用说明</button><button id="open-button" class="primary">${icon('open')}打开模型</button></div>
  </header>
  <main class="workspace">
    <aside class="left-panel panel" aria-label="模型构件">
      <div class="panel-title"><h2>模型结构</h2><span id="component-count" class="count">0</span></div>
      <div class="filters"><label class="search-field">${icon('search')}<input id="search" type="search" placeholder="搜索名称、ID 或族" aria-label="搜索构件" disabled /></label>
        <label class="filter-label">楼层<select id="level-filter" aria-label="按楼层筛选" disabled><option value="">全部楼层</option></select></label>
        <label class="filter-label">类别<select id="category-filter" aria-label="按类别筛选" disabled><option value="">全部类别</option></select></label>
      </div>
      <div class="list-heading"><span id="list-summary">尚未打开模型</span><button id="show-filtered" class="text-button" disabled>隔离筛选结果</button></div>
      <div id="component-list" class="component-list"><p class="panel-empty">打开模型后，在这里浏览构件和楼层。</p></div>
      <div id="list-more" class="list-more" hidden><button id="more-button" class="quiet">显示更多构件</button></div>
    </aside>
    <section class="viewport-panel" aria-label="三维模型">
      <div class="viewer-toolbar" role="toolbar" aria-label="查看工具">
        <div class="toolbar-group"><button id="fit-all" title="显示整个模型（Home）" disabled>${icon('fit')}全部居中</button><button id="fit-selection" title="定位选中构件（F）" disabled>${icon('fit')}定位</button></div>
        <div class="toolbar-group"><button id="hide-selection" title="隐藏选中构件（H）" disabled>${icon('hide')}隐藏</button><button id="isolate-selection" title="只显示选中构件（I）" disabled>${icon('isolate')}隔离</button><button id="show-all" title="恢复显示全部构件" disabled>${icon('eye')}显示全部</button></div>
        <button id="close-model" class="toolbar-close" title="关闭模型" disabled>${icon('close')}<span class="sr-only">关闭模型</span></button>
      </div>
      <div class="viewer-toolsbar" role="toolbar" aria-label="模型分析工具">
        <span id="selection-summary">未选择构件</span><label class="ghost-control"><input id="ghost-mode" type="checkbox" disabled />隔离时保留透明背景</label>
        <div class="tools-buttons"><button id="navigation-tool" disabled>漫游与飞行</button><button id="section-tool" disabled>剖切盒</button><button id="measurement-tool" disabled>测量</button><button id="views-tool" disabled>视点</button><button id="audit-tool" disabled>统计与审查</button><button id="quantity-tool" disabled>构件量表</button><button id="assistant-tool">模型助手</button></div>
      </div>
      <section id="tools-panel" class="tools-panel" aria-label="模型工具" hidden>
        <div class="tools-heading"><h2 id="tools-title">模型工具</h2><button id="tools-close" class="icon-button" aria-label="关闭模型工具">${icon('close')}</button></div>
        <div id="section-controls" class="tool-content" hidden><p>用六个边界控制查看范围，或围绕选中构件剖切。</p><div class="tool-actions"><button id="section-selection" class="quiet">剖切所选</button><button id="section-all" class="quiet">剖切全部</button><button id="section-clear" class="quiet">关闭剖切</button></div><label class="tool-field">选中构件边距<input id="section-padding" type="number" min="0" value="0" step="any" /></label><div class="section-coordinates"><span></span><span>最小值</span><span>最大值</span>${['X', 'Y', 'Z'].map(axis => `<label>${axis}</label><input id="section-min-${axis.toLowerCase()}" aria-label="${axis} 最小值" type="number" step="any" /><input id="section-max-${axis.toLowerCase()}" aria-label="${axis} 最大值" type="number" step="any" />`).join('')}</div><button id="section-apply" class="primary">应用剖切范围</button><p id="section-status" class="tool-note">尚未启用剖切；坐标使用模型原始单位。</p></div>
        <div id="measurement-controls" class="tool-content" hidden><p>开启后依次点击模型表面的两个点，计算直线距离。</p><div class="tool-actions"><button id="measurement-start" class="quiet">开始测量</button><button id="measurement-stop" class="quiet">结束拾点</button><button id="measurement-clear" class="quiet">清除测量</button></div><label class="tool-field">模型长度单位<select id="source-unit"><option value="unknown">未知，保留模型单位</option><option value="m">米（m）</option><option value="mm">毫米（mm）</option><option value="ft">英尺（ft）</option></select></label><label class="tool-field">显示单位<select id="measurement-unit"><option value="model">模型单位</option><option value="m">米（m）</option><option value="mm">毫米（mm）</option><option value="ft">英尺（ft）</option></select></label><output id="measurement-result" class="measurement-result">尚未测量</output><p id="measurement-note" class="tool-note">旧模型包未声明单位时，请核对源模型后指定。当前是表面两点直线距离，不含顶点或边吸附。</p></div>
        <div id="views-controls" class="tool-content" hidden><p>保存相机、选择、显示和剖切状态，视点保存在本机。</p><label class="tool-field">视点名称<input id="viewpoint-name" maxlength="80" placeholder="例如：二层设备检查" /></label><button id="viewpoint-save" class="primary">保存当前视点</button><label class="tool-field">已保存视点<select id="viewpoint-select" size="5" aria-label="已保存视点"></select></label><div class="tool-actions"><button id="viewpoint-restore" class="quiet">恢复视点</button><button id="viewpoint-delete" class="quiet">删除视点</button></div><p id="viewpoint-note" class="tool-note">同一模型重新打开后可恢复；关闭程序不会删除保存的视点。</p></div>
        <div id="audit-controls" class="tool-content" hidden><p id="audit-summary">统计当前筛选结果。点击类别筛选，点击构件定位。</p><h3>按类别统计</h3><div id="category-summary" class="category-summary"></div><h3>三角面最多的构件</h3><div id="heavy-components" class="heavy-components"></div><p class="tool-note">面数按导出几何及实例累计，可辅助寻找高开销构件，不等同于工程量。碰撞检测、模型版本对比和参数审查在后续阶段交付。</p></div>
        <div id="quantity-controls" class="tool-content" hidden></div>
        <div id="navigation-controls" class="tool-content" hidden>
          <p>人物漫游采用第三人称跟随视角；飞行可自由查看模型。</p>
          <label class="tool-field">导航模式<select id="navigation-mode"><option value="orbit">旋转查看</option><option value="walk">人物漫游</option><option value="fly">自由飞行</option></select></label>
          <label class="tool-field">模型长度单位<select id="navigation-unit"><option value="unknown">未知，请先核对</option><option value="m">米（m）</option><option value="mm">毫米（mm）</option><option value="ft">英尺（ft）</option></select></label>
          <div class="navigation-settings"><label class="tool-field">移动速度（m/s）<input id="navigation-speed" type="number" min="0.1" max="10" step="0.1" value="1.4" /></label><label class="tool-field">人物眼高（m）<input id="navigation-height" type="number" min="0.8" max="2.2" step="0.05" value="1.7" /></label></div>
          <div class="tool-actions"><button id="navigation-place" class="quiet">重新放置人物</button><button id="navigation-exit" class="quiet">退出导航</button></div>
          <p id="navigation-status" class="navigation-status" role="status">打开模型后可使用人物漫游和飞行。</p>
          <p class="tool-note">WASD 移动，右键拖动环看，Shift 加速；飞行时 Q/E 升降。点击三维窗口后启用键盘，Esc 返回旋转查看。</p>
          <p class="tool-note">人物需放置在可确认的模型表面。当前为基础表面检测与射线阻挡，复杂楼梯、狭缝和完整身体碰撞尚待扩展。</p>
        </div>
        <div id="assistant-controls" class="tool-content" hidden></div>
      </section>
      <div id="viewport" class="viewport"></div>
      <div id="welcome" class="welcome"><div class="welcome-card"><div class="welcome-icon">${icon('cube')}</div><span class="eyebrow">模型工作台</span><h2>把模型带到眼前</h2><p>直接打开模型 ZIP，<br />查看几何、楼层与构件信息。</p><button id="welcome-open" class="primary">${icon('open')}选择模型文件</button><button id="demo-button" class="demo-link">先体验演示模型 <span aria-hidden="true">↗</span></button><span class="welcome-note">模型最大 2 GB · 本机处理 · 支持拖放文件</span></div></div>
      <div id="load-overlay" class="load-overlay" hidden><div class="load-card"><span class="eyebrow">正在打开模型</span><h2 id="load-phase">读取文件</h2><progress id="load-progress" max="100" value="0"></progress><div class="load-bottom"><span id="load-percent">0%</span><button id="cancel-load" class="text-button">取消加载</button></div></div></div>
      <div id="drop-overlay" class="drop-overlay" hidden><div>${icon('open')}松开以打开模型</div></div>
      <div class="viewport-bottom"><span id="model-label">未打开模型</span><span class="navigation-hint">左键旋转 · 滚轮缩放 · 右键平移</span><span class="axis-note">Z ↑</span></div>
    </section>
    <aside class="right-panel panel" aria-label="构件属性"><div class="panel-title"><h2>构件信息</h2><button id="clear-selection" class="icon-button" title="取消选择" disabled>${icon('close')}</button></div><div id="properties" class="properties"><div class="property-placeholder">${icon('cube')}<h3>查看构件信息</h3><p>点击模型或左侧列表中的构件，<br />查看它的名称、类别和参数。</p></div></div></aside>
  </main>
  <footer class="app-footer"><span id="footer-status" role="status" aria-live="polite">准备就绪</span><span id="model-stats"></span><span id="company-attribution"></span><span id="version-label"></span></footer>
  <input id="file-input" type="file" accept=".zip,application/zip" hidden />
  <div id="toast" class="toast" role="status" hidden></div>
  <section id="load-error" class="import-error" role="alert" hidden><strong>模型未能打开</strong><p id="load-error-message"></p><button id="dismiss-error" class="quiet">关闭提示</button></section>
  <dialog id="archive-dialog"><div class="dialog-heading"><h2>选择包内模型</h2><button id="archive-cancel" class="icon-button" aria-label="取消选择模型">${icon('close')}</button></div><form id="archive-form" class="archive-content"><p id="archive-summary"></p><label for="archive-select">模型</label><select id="archive-select" size="9" required></select><p id="archive-hint">请选择已导出的模型 ZIP。原始 RVT 暂不支持独立读取，不会启动外部建模软件。</p><button type="submit" class="primary">打开选中模型</button></form></dialog>
  <dialog id="help-dialog"><div class="dialog-heading"><h2>使用说明</h2><button id="close-help" class="icon-button" aria-label="关闭使用说明">${icon('close')}</button></div><div class="help-content"><p>选择已导出的模型 ZIP 或模型总包，也可直接拖进窗口。全部解析、显示和属性查询均在当前设备进行。</p><h3>查看与选择</h3><p>左键拖动旋转，右键拖动平移，滚轮缩放。点击构件查看属性；使用“定位”“隐藏”“隔离”调整视图。“显示全部”恢复隐藏的构件。</p><h3>支持范围</h3><p>模型 ZIP 的文件上限为 2 GB（2,147,483,648 字节），在当前设备分段解析。总包可直接打开，再选择包内模型。查看器不会启动任何 Revit；原始 RVT 的独立读取尚未实现。</p><h3>数据核对</h3><p>属性按文件中的实例、类型、族信息原样展示。缺失或无效引用会在模型概览中提示；当前仍需对照原 RVT 验收坐标、单位和参数完整性。</p><h3>快捷键</h3><p>Home：全部居中　F：定位　H：隐藏　I：隔离　Esc：取消选择</p></div></dialog>
`;
const revitHelp = document.createElement('section');
revitHelp.innerHTML = '<h3>Revit导出插件</h3><p>Windows 桌面版包含 Revit 导出插件，可在这里查看安装状态并安装或修复。</p><p>没有 Revit 也能直接查看已导出的模型 ZIP。</p><button id="help-revit-button" class="quiet">管理Revit插件</button>';
$('help-dialog').querySelector('.help-content')!.append(revitHelp);
const navigationHelp = document.createElement('section');
navigationHelp.innerHTML = '<h3>人物漫游与飞行</h3><p>打开“漫游与飞行”，核对模型长度单位。选择“人物漫游”后点击模型地面放置人物；WASD 行走，右键环看，Shift 加速。选择“自由飞行”后点击三维窗口启用，Q/E 升降；Esc 返回旋转查看。</p><h3>本地模型助手</h3><p>打开“模型助手”，可查询模型概览、类别数量、楼层分布、所选属性、缺失项和原始量，也可输入“查找：关键词”或“缺少参数：参数名”。结果附构件定位依据；切换模型或查询范围后请重新查询。此版本使用本地有限规则查询，模型数据不上传。</p>';
$('help-dialog').querySelector('.help-content')!.append(navigationHelp);
const companyHelp = document.createElement('section');
companyHelp.id = 'company-information';
for (const [tag, id] of [['h3', 'brand-license-heading'], ['p', 'brand-attribution'], ['p', 'brand-license-summary']] as const) {
    const element = document.createElement(tag); element.id = id; companyHelp.append(element);
}
$('help-dialog').querySelector('.help-content')!.append(companyHelp);
const applyStaticTranslations = bindStaticTranslations(app);
const viewportPanel = document.querySelector<HTMLElement>('.viewport-panel')!;
const updateToolInsets = () => {
    const height = [...viewportPanel.querySelectorAll<HTMLElement>('.viewer-toolbar,.viewer-toolsbar')]
        .reduce((sum, toolbar) => sum + toolbar.getBoundingClientRect().height, 0);
    viewportPanel.style.setProperty('--tools-offset', `${height}px`);
};
const toolbarResizeObserver = new ResizeObserver(updateToolInsets);
for (const toolbar of viewportPanel.querySelectorAll('.viewer-toolbar,.viewer-toolsbar')) toolbarResizeObserver.observe(toolbar);
updateToolInsets();
document.documentElement.lang = getLanguage() === 'en' ? 'en' : 'zh-CN';
applyStaticTranslations();
mountUpdateUI();
mountRevitUI();
applyBrand();
document.querySelector('.brand-mark')!.textContent = brand.monogram;
let model: BimModel | null = null;
let selectedId: string | null = null;
let componentMap = new Map<string, number>();
let viewer: ModelViewer;
let worker: Worker | null = null;
let loadId = 0;
let loading = false;
let readingArchive = false;
let propertyTab: 'properties' | 'typeProperties' | 'familyProperties' = 'properties';
let toastTimer: ReturnType<typeof setTimeout>;
let allFiltered: BimComponent[] = [];
let viewerUnavailable = false;
let indexWorker: Worker | null = null;
let rejectIndexBuild: ((reason: Error) => void) | null = null;
let indexRequest = 0;
let indexResult: IndexResult | null = null;
let treeRows: Int32Array = new Int32Array();
let triangleCounts = new Float64Array();
let collapsedGroups = new Set<string>();
let selectedIds = new Set<string>();
let selecting = false;
let selectionAnchor = -1;
let listRenderFrame = 0;
let measuring = false;
const toolIds = ['section', 'measurement', 'views', 'audit', 'quantity', 'navigation', 'assistant'] as const;
let activeTool: typeof toolIds[number] | null = null;
let viewStorageKey = '';
let savedViews: SavedView[] = [];
let currentPhase = '读取文件';
let lastToastMessage = '';
let lastImportError = '';
let currentArchiveChoices: ArchiveChoice[] | null = null;
let filtering = false;
const TREE_ROW_HEIGHT = 48;
const componentForId = (id: string) => model?.components[componentMap.get(id) ?? -1];
const quantityPanel = new QuantityPanel($('quantity-controls'), () => ({
    modelName: model?.name ?? '', all: model?.components ?? [], filtered: allFiltered,
    selected: [...selectedIds].map(componentForId).filter((component): component is BimComponent => !!component),
    filterDescription: [
        $<HTMLSelectElement>('level-filter').value || tr('全部楼层'),
        $<HTMLSelectElement>('category-filter').value || tr('全部类别'),
        $<HTMLInputElement>('search').value ? tr('搜索：{0}', { '0': $<HTMLInputElement>('search').value }) : '',
    ].filter(Boolean).join(' · '),
    filtering,
}), ids => { selectMany(ids); viewer.focusSelection(); }, notify);
function renderQuantities() { if (model && activeTool === 'quantity') quantityPanel.refresh(); }
const assistantPanel = new AssistantPanel($('assistant-controls'), {
    getContext: () => ({ model, filtered: allFiltered,
        selected: [...selectedIds].map(componentForId).filter((component): component is BimComponent => !!component),
        filtering: filtering || loading,
        filterKey: JSON.stringify([$<HTMLSelectElement>('level-filter').value, $<HTMLSelectElement>('category-filter').value, $<HTMLInputElement>('search').value]),
        filterDescription: [$<HTMLSelectElement>('level-filter').value || tr('全部楼层'),
            $<HTMLSelectElement>('category-filter').value || tr('全部类别'), $<HTMLInputElement>('search').value].filter(Boolean).join(' · '),
    }),
    onLocate: (ids: readonly string[]) => { if (!model || loading || viewerUnavailable) return; selectMany(ids); viewer.focusSelection(); },
    getLanguage,
});
function refreshAssistant() { assistantPanel.refresh(); }
function refreshNavigation() {
    if (!viewer || viewerUnavailable) return;
    const state = viewer.getNavigationState();
    const viewport = $('viewport');
    Object.assign(viewport.dataset, { navigationMode: state.mode, navigationActive: String(state.active),
        navigationNeedsGroundPlacement: String(state.needsGroundPlacement), navigationAvatarVisible: String(state.avatarVisible) });
    $<HTMLSelectElement>('navigation-mode').value = state.mode;
    $<HTMLSelectElement>('navigation-unit').value = state.unit;
    $<HTMLInputElement>('navigation-speed').value = String(state.speedMetersPerSecond);
    $<HTMLInputElement>('navigation-height').value = String(state.eyeHeightMeters);
    $<HTMLButtonElement>('navigation-exit').disabled = state.mode === 'orbit';
    $<HTMLButtonElement>('navigation-place').disabled = state.mode !== 'walk';
    $<HTMLSelectElement>('navigation-mode').disabled = !model || loading;
    let message: string;
    if (!model || state.reason === 'no-model') message = tr('打开模型后可使用人物漫游和飞行。');
    else if (state.reason === 'unknown-unit') message = tr('请先核对并指定模型长度单位，再进入导航。');
    else if (state.mode === 'orbit') message = tr('旋转查看模式。可选择人物漫游或自由飞行。');
    else if (state.needsGroundPlacement) message = tr('请在三维窗口点击可见地面，放置人物起点。');
    else if (state.groundStatus === 'blocked') message = tr('前方表面或障碍未通过当前检测，本次移动已停止。');
    else if (!state.active) message = tr('点击三维窗口以启用移动；输入框和面板中不会行走。');
    else message = state.mode === 'walk' ? tr('人物漫游中 · WASD 移动 · 右键环看 · Esc 退出') : tr('自由飞行中 · WASD 移动 · Q/E 升降 · Esc 退出');
    $('navigation-status').textContent = message;
    const hint = document.querySelector<HTMLElement>('.navigation-hint');
    if (hint) hint.textContent = state.mode === 'orbit' ? tr('左键旋转 · 滚轮缩放 · 右键平移')
        : state.mode === 'walk' ? tr('人物漫游 · WASD 移动 · 右键环看 · Esc 退出') : tr('自由飞行 · WASD / Q E · Esc 退出');
}
function notify(message: string) {
    lastToastMessage = message;
    $('toast').textContent = translatedMessage(message);
    $('toast').hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { $('toast').hidden = true; }, 5000);
}
function reportImportError(message: string, finishLoading = true) {
    if (finishLoading) {
        loading = false;
        readingArchive = false;
        updateActions();
    }
    lastImportError = message;
    $('load-error-message').textContent = translatedMessage(message, true);
    $('load-error').hidden = false;
    notify(message);
}
$('dismiss-error').onclick = () => { $('load-error').hidden = true; };
let chooseArchive: ((choice: ArchiveChoice | null) => void) | null = null;
function selectArchive(choices: ArchiveChoice[]): Promise<ArchiveChoice | null> {
    currentArchiveChoices = choices;
    const dialog = $<HTMLDialogElement>('archive-dialog');
    $('archive-summary').textContent = tr("包内找到 {0} 个模型，请选择要打开的一个。", { "0": choices.length.toLocaleString(locale()) });
    const select = $<HTMLSelectElement>('archive-select');
    select.replaceChildren();
    for (const [index, choice] of choices.entries()) {
        const size = choice.size ? ` · ${(choice.size / 1024 / 1024).toFixed(1)} MB` : '';
        const option = new Option(`${choice.name}${choice.kind === 'rvt' ? tr(" · RVT 暂未支持直读") : ''}${size}`, String(index));
        option.disabled = choice.kind === 'rvt';
        select.add(option);
    }
    select.selectedIndex = choices.findIndex(choice => choice.kind !== 'rvt');
    $<HTMLButtonElement>('archive-form').querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled = select.selectedIndex < 0;
    $<HTMLFormElement>('archive-form').onsubmit = event => { event.preventDefault(); finishArchiveChoice(choices[Number(select.value)] ?? null); };
    dialog.showModal();
    return new Promise(resolve => { chooseArchive = resolve; });
}
function refreshArchiveLabels() {
    if (!currentArchiveChoices) return;
    const choices = currentArchiveChoices;
    const select = $<HTMLSelectElement>('archive-select');
    $('archive-summary').textContent = tr('包内找到 {0} 个模型，请选择要打开的一个。', { '0': choices.length.toLocaleString(locale()) });
    for (const [index, choice] of choices.entries()) {
        const size = choice.size ? ` · ${(choice.size / 1024 / 1024).toFixed(1)} MB` : '';
        select.options[index]!.textContent = `${choice.name}${choice.kind === 'rvt' ? tr(' · RVT 暂未支持直读') : ''}${size}`;
    }
}
function finishArchiveChoice(choice: ArchiveChoice | null) {
    $<HTMLDialogElement>('archive-dialog').close();
    const resolve = chooseArchive;
    chooseArchive = null;
    currentArchiveChoices = null;
    resolve?.(choice);
}
$('archive-cancel').onclick = () => finishArchiveChoice(null);
$<HTMLDialogElement>('archive-dialog').addEventListener('cancel', event => { event.preventDefault(); finishArchiveChoice(null); });
function setProgress(phase: string, progress: number) {
    currentPhase = phase;
    $('load-phase').textContent = translatedProgress(phase);
    const value = Math.min(100, Math.max(0, Math.round(progress)));
    ($('load-progress') as HTMLProgressElement).value = value;
    $('load-percent').textContent = `${value}%`;
}
function updateActions() {
    for (const id of ['open-button', 'welcome-open', 'demo-button'])
        $<HTMLButtonElement>(id).disabled = loading || viewerUnavailable;
    for (const id of ['fit-all', 'show-all', 'close-model', 'search', 'level-filter', 'category-filter', 'show-filtered']) {
        ($<HTMLButtonElement>(id)).disabled = !model || loading;
    }
    for (const id of ['fit-selection', 'hide-selection', 'isolate-selection', 'clear-selection']) {
        ($<HTMLButtonElement>(id)).disabled = !selectedIds.size || loading;
    }
    const selectedGeometry = !viewerUnavailable && [...selectedIds].some(id => viewer.getComponentBounds(id) !== null);
    for (const id of ['fit-selection', 'isolate-selection', 'section-selection'])
        $<HTMLButtonElement>(id).disabled = !selectedGeometry || loading;
    for (const id of ['ghost-mode', 'section-tool', 'measurement-tool', 'views-tool', 'audit-tool', 'quantity-tool', 'navigation-tool'])
        $<HTMLButtonElement>(id).disabled = !model || loading;
    $<HTMLButtonElement>('assistant-tool').disabled = loading;
    $('selection-summary').textContent = selectedIds.size ? tr("已选 {0} 个构件 · Ctrl 点击多选", { "0": selectedIds.size.toLocaleString(locale()) }) : tr("Ctrl 点击可多选");
    $('load-overlay').hidden = !loading;
    $('welcome').hidden = !!model || loading;
    renderQuantities();
    refreshNavigation();
    refreshAssistant();
    if (model) {
        const hidden = viewer.getHiddenCount();
        const ghosted = viewer.getStats().ghostedComponents;
        const hiddenOnly = Math.max(0, hidden - ghosted);
        $('footer-status').textContent = [hiddenOnly ? tr("已隐藏 {0} 个构件", { "0": hiddenOnly.toLocaleString(locale()) }) : '', ghosted ? tr("{0} 个透明背景构件", { "0": ghosted.toLocaleString(locale()) }) : ''].filter(Boolean).join(' · ') || tr("模型已就绪");
        requestAnimationFrame(() => {
            if (!model)
                return;
            const stats = viewer.getStats();
            $('model-stats').textContent = tr("{0} 构件 · {1} 三角面", { "0": model.components.length.toLocaleString(locale()), "1": stats.triangles.toLocaleString(locale()) });
            $('model-stats').title = tr("{0} 次绘制 · {1} 个空间批次 · {2} 个复用实例 · {3} 个有几何的显示构件。三角面为 GPU 提交数量，可能包含隐藏的面。渲染数组 {4} MiB；空间索引估计 {5} MiB；最近拾取 {6} 批 / {7} 面 / {8} ms。", { "0": stats.drawCalls.toLocaleString(locale()), "1": stats.batches.toLocaleString(locale()), "2": stats.instancedPlacements.toLocaleString(locale()), "3": stats.renderedComponents.toLocaleString(locale()), "4": (stats.renderBufferBytes / 1048576).toFixed(1), "5": (stats.spatialIndexBytesEstimate / 1048576).toFixed(1), "6": stats.lastPickBatches, "7": stats.lastPickTriangles, "8": stats.lastPickMilliseconds.toFixed(2) });
            Object.assign($('model-stats').dataset, { batches: String(stats.batches), instancedPlacements: String(stats.instancedPlacements), renderBufferBytes: String(stats.renderBufferBytes), pickBatches: String(stats.lastPickBatches), pickTriangles: String(stats.lastPickTriangles), pickMilliseconds: String(stats.lastPickMilliseconds) });
        });
    }
}
function syncSelection() {
    if (viewerUnavailable)
        return;
    selectedIds = new Set(viewer.getSelectedIds());
    selectedId = viewer.getSelectedId();
    for (const row of document.querySelectorAll<HTMLElement>('.component-row')) {
        const selected = selectedIds.has(row.dataset.id ?? '');
        row.classList.toggle('selected', selected);
        row.querySelector('.component-main')?.setAttribute('aria-pressed', String(selected));
    }
    renderProperties();
    updateActions();
}
function selectMany(ids: Iterable<string>, mode: 'replace' | 'add' | 'toggle' = 'replace', reveal = true) {
    if (viewerUnavailable)
        return;
    const valid = [...ids].filter(id => componentMap.has(id));
    selecting = true;
    try {
        if (reveal)
            viewer.setVisible(valid, true);
        viewer.selectMany(valid, mode);
    }
    finally {
        selecting = false;
    }
    syncSelection();
    renderList();
}
function select(id: string | null) { selectMany(id ? [id] : []); }
function filterList() {
    if (!model || !indexWorker)
        return;
    const request = ++indexRequest;
    filtering = true;
    renderQuantities();
    refreshAssistant();
    $('list-summary').textContent = tr("正在筛选…");
    indexWorker.postMessage({ type: 'query', request, filter: {
            search: $<HTMLInputElement>('search').value,
            level: $<HTMLSelectElement>('level-filter').value,
            category: $<HTMLSelectElement>('category-filter').value,
        } });
}
function receiveIndex(result: IndexResult) {
    if (!model)
        return;
    indexResult = result;
    filtering = false;
    allFiltered = Array.from(result.indices, index => model!.components[index]!);
    treeRows = visibleTreeRows(result, collapsedGroups);
    selectionAnchor = -1;
    $('component-list').scrollTop = 0;
    $('list-summary').textContent = tr("{0} 个构件", { "0": allFiltered.length.toLocaleString(locale()) });
    $('list-more').hidden = true;
    $('show-filtered').setAttribute('aria-label', tr("隔离筛选得到的 {0} 个构件", { "0": allFiltered.length }));
    renderList();
    renderAudit();
    renderQuantities();
    refreshAssistant();
}
function selectTreeGroup(group: TreeGroup, additive: boolean) {
    if (!model || !indexResult)
        return;
    const ids = Array.from(indexResult.indices.subarray(group.first, group.last), index => model!.components[index]!.id);
    const mode = additive ? (ids.every(id => selectedIds.has(id)) ? 'toggle' : 'add') : 'replace';
    selectMany(ids, mode);
}
function renderList() {
    const list = $('component-list');
    if (!model || !indexResult)
        return;
    if (!treeRows.length) {
        list.replaceChildren(textElement('p', tr("没有符合条件的构件"), 'panel-empty'));
        return;
    }
    const scrollTop = Math.min(list.scrollTop, Math.max(0, treeRows.length * TREE_ROW_HEIGHT - list.clientHeight));
    const first = Math.max(0, Math.floor(scrollTop / TREE_ROW_HEIGHT) - 5);
    const last = Math.min(treeRows.length, first + Math.ceil((list.clientHeight || 500) / TREE_ROW_HEIGHT) + 12);
    const spacer = document.createElement('div');
    spacer.className = 'tree-spacer';
    spacer.style.height = `${treeRows.length * TREE_ROW_HEIGHT}px`;
    const window = document.createElement('div');
    window.className = 'tree-window';
    window.style.transform = `translateY(${first * TREE_ROW_HEIGHT}px)`;
    for (let rowIndex = first; rowIndex < last; rowIndex++) {
        const reference = treeRows[rowIndex]!;
        if (reference < 0) {
            const group = indexResult.groups[-reference - 1]!;
            const sourceLabel = (JSON.parse(group.key) as string[])[group.depth];
            const groupLabel = sourceLabel ? group.label : tr(['未分类', '未提供族', '未提供类型'][group.depth]!);
            const row = document.createElement('div');
            row.className = 'tree-group';
            row.style.paddingLeft = `${group.depth * 11}px`;
            const toggle = document.createElement('button');
            toggle.className = 'tree-toggle';
            toggle.textContent = collapsedGroups.has(group.key) ? '›' : '⌄';
            toggle.setAttribute('aria-label', `${collapsedGroups.has(group.key) ? tr("展开") : tr("折叠")} ${groupLabel}`);
            toggle.setAttribute('aria-expanded', String(!collapsedGroups.has(group.key)));
            toggle.onclick = () => { if (collapsedGroups.has(group.key))
                collapsedGroups.delete(group.key);
            else
                collapsedGroups.add(group.key); treeRows = visibleTreeRows(indexResult!, collapsedGroups); selectionAnchor = -1; renderList(); };
            const button = document.createElement('button');
            button.className = 'tree-group-main';
            button.title = tr("选择本组 {0} 个构件", { "0": group.count.toLocaleString(locale()) });
            button.append(textElement('span', groupLabel), textElement('span', group.count.toLocaleString(locale()), 'tree-group-count'));
            button.onclick = event => selectTreeGroup(group, event.ctrlKey || event.metaKey);
            row.append(toggle, button);
            window.append(row);
            continue;
        }
        const c = model.components[reference]!;
        const row = document.createElement('div');
        row.className = 'component-row';
        row.dataset.id = c.id;
        row.classList.toggle('selected', selectedIds.has(c.id));
        const button = document.createElement('button');
        button.className = 'component-main';
        button.setAttribute('aria-pressed', String(selectedIds.has(c.id)));
        const name = document.createElement('span');
        name.className = 'component-name';
        name.textContent = c.name || c.type || c.id;
        name.title = name.textContent;
        const subtitle = document.createElement('span');
        subtitle.className = 'component-subtitle';
        subtitle.textContent = `${c.category || tr("未分类")} · ${c.id}`;
        button.append(name, subtitle);
        button.onclick = event => {
            if (event.shiftKey && selectionAnchor >= 0) {
                const refs = treeRows.subarray(Math.min(selectionAnchor, rowIndex), Math.max(selectionAnchor, rowIndex) + 1);
                selectMany(Array.from(refs).filter(index => index >= 0).map(index => model!.components[index]!.id), event.ctrlKey ? 'add' : 'replace');
            }
            else
                selectMany([c.id], event.ctrlKey || event.metaKey ? 'toggle' : 'replace');
            selectionAnchor = rowIndex;
        };
        button.ondblclick = () => { select(c.id); viewer.focus(c.id); };
        const visible = viewer.isComponentVisible(c.id);
        const eye = document.createElement('button');
        eye.className = 'row-eye';
        eye.innerHTML = icon(visible ? 'eye' : 'hide');
        eye.setAttribute('aria-label', tr("{0}构件 {1}", { "0": visible ? tr("隐藏") : tr("显示"), "1": c.id }));
        eye.onclick = () => { viewer.setVisible([c.id], !visible); renderList(); updateActions(); };
        row.classList.toggle('is-hidden', !visible);
        row.append(button, eye);
        window.append(row);
    }
    spacer.append(window);
    list.replaceChildren(spacer);
    list.scrollTop = scrollTop;
}
$('component-list').addEventListener('scroll', () => {
    if (listRenderFrame)
        return;
    listRenderFrame = requestAnimationFrame(() => { listRenderFrame = 0; renderList(); });
}, { passive: true });
function textElement(tag: string, value: string, className?: string) {
    const el = document.createElement(tag);
    el.textContent = value;
    if (className)
        el.className = className;
    return el;
}
function renderOverview() {
    const target = $('properties');
    target.replaceChildren();
    if (!model) {
        const block = textElement('div', '', 'property-placeholder');
        block.innerHTML = `${icon('cube')}<h3>${tr('查看构件信息')}</h3><p>${tr('点击模型或左侧列表中的构件，')}<br />${tr('查看它的名称、类别和参数。')}</p>`;
        target.append(block);
        return;
    }
    const heading = textElement('div', '', 'model-overview');
    heading.append(textElement('span', tr("模型概览"), 'eyebrow'), textElement('h3', model.name), textElement('p', tr("选择构件，查看文件中保留的参数。"), 'muted'));
    const stats = document.createElement('dl');
    stats.className = 'overview-stats';
    for (const [label, value] of [[tr("构件"), model.components.length], [tr("楼层"), model.levels.length], [tr("几何记录"), model.stats.geometries]]) {
        stats.append(textElement('dt', String(label)), textElement('dd', Number(value).toLocaleString(locale())));
    }
    heading.append(stats);
    target.append(heading);
    if (model.warnings.length) {
        const warning = document.createElement('details');
        warning.className = 'model-warnings';
        warning.open = true;
        warning.append(textElement('summary', tr("数据提示 · {0}", { "0": model.warnings.length })));
        if (getLanguage() === 'en') warning.append(textElement('p', tr('源诊断可能保留原文。'), 'diagnostic-note'));
        for (const message of model.warnings)
            warning.append(textElement('p', message));
        target.append(warning);
    }
}
function renderGroups(groups: PropertyGroups, target: HTMLElement) {
    const entries = Object.entries(groups);
    if (!entries.length) {
        target.append(textElement('p', tr("当前模型未提供这类属性。"), 'panel-empty'));
        return;
    }
    for (const [index, [name, values]] of entries.entries()) {
        const section = document.createElement('details');
        section.className = 'property-group';
        section.open = index < 3;
        const summary = document.createElement('summary');
        summary.append(textElement('span', name), textElement('span', `${Object.keys(values).length}`, 'property-count'));
        section.append(summary);
        const dl = document.createElement('dl');
        for (const [key, value] of Object.entries(values)) {
            const formatted = value === null || value === undefined ? tr("未提供") : typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value);
            dl.append(textElement('dt', key), textElement('dd', formatted));
        }
        section.append(dl);
        target.append(section);
    }
}
function renderProperties() {
    const c = selectedId ? componentForId(selectedId) : undefined;
    if (!c) {
        renderOverview();
        return;
    }
    const target = $('properties');
    target.replaceChildren();
    if (selectedIds.size > 1) {
        const block = textElement('div', '', 'multi-selection');
        block.append(textElement('span', tr("当前选择集合"), 'eyebrow'), textElement('h3', tr("已选 {0} 个构件", { "0": selectedIds.size.toLocaleString(locale()) })));
        let triangles = 0;
        const categories = new Set<string>();
        for (const id of selectedIds) {
            const index = componentMap.get(id);
            if (index !== undefined) {
                triangles += triangleCounts[index] ?? 0;
                categories.add(model!.components[index]!.category);
            }
        }
        block.append(textElement('p', tr("{0} 个类别 · {1} 个三角面", { "0": categories.size.toLocaleString(locale()), "1": triangles.toLocaleString(locale()) }), 'muted'), textElement('p', tr("定位、隐藏、隔离和剖切均作用于当前选择集合。Ctrl 点击增减选择，单击一个构件查看具体属性。"), 'muted'));
        target.append(block);
        return;
    }
    const header = textElement('div', '', 'selected-heading');
    header.append(textElement('span', c.category || tr("未分类"), 'eyebrow'), textElement('h3', c.name || c.id));
    header.querySelector('.eyebrow')!.setAttribute('title', c.categoryCode ?? c.category);
    const idRow = textElement('div', '', 'id-row');
    idRow.append(textElement('code', c.id));
    const copy = document.createElement('button');
    copy.className = 'icon-button';
    copy.innerHTML = icon('copy');
    copy.title = tr("复制构件 ID");
    copy.onclick = async () => { try {
        await navigator.clipboard.writeText(c.id);
        notify(tr("构件 ID 已复制"));
    }
    catch {
        notify(tr("构件 ID：{0}", { "0": c.id }));
    } };
    idRow.append(copy);
    header.append(idRow);
    const metadata = document.createElement('dl');
    metadata.className = 'component-metadata';
    for (const [label, value] of [[tr("族"), c.family], [tr("类型"), c.type], [tr("楼层"), c.levelNames.join(tr("、"))]])
        metadata.append(textElement('dt', label), textElement('dd', value || tr("未提供")));
    header.append(metadata);
    target.append(header);
    const tabs = document.createElement('div');
    tabs.className = 'property-tabs';
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', tr("属性来源"));
    for (const [key, label] of [['properties', tr("实例")], ['typeProperties', tr("类型")], ['familyProperties', tr("族")]] as const) {
        const button = document.createElement('button');
        button.textContent = label;
        button.classList.toggle('active', propertyTab === key);
        button.setAttribute('role', 'tab');
        button.setAttribute('aria-selected', String(propertyTab === key));
        button.onclick = () => { propertyTab = key; renderProperties(); };
        tabs.append(button);
    }
    target.append(tabs);
    const groups = textElement('div', '', 'property-groups');
    renderGroups(c[propertyTab], groups);
    target.append(groups);
}
async function prepareIndex(next: BimModel, id: number) {
    const records: IndexRecord[] = [];
    const faces = new Float64Array(next.components.length);
    let start = performance.now();
    for (let index = 0; index < next.components.length; index++) {
        const component = next.components[index]!;
        let triangles = 0;
        for (const nodeId of component.nodeIds) {
            const node = next.nodes[nodeId];
            if (node)
                triangles += (next.geometries[node.geometryId]?.indices.length ?? 0) / 3;
        }
        faces[index] = triangles;
        records.push({ id: component.id, name: component.name, category: component.category, categoryCode: component.categoryCode ?? '',
            family: component.family, type: component.type, levels: component.levelNames, triangles });
        if (performance.now() - start > 12) {
            await new Promise(resolve => setTimeout(resolve, 0));
            if (id !== loadId)
                throw new Error(tr("构件索引加载已取消。"));
            start = performance.now();
        }
    }
    triangleCounts = faces;
    const worker = new Worker(new URL('./component-index.worker.ts', import.meta.url), { type: 'module' });
    indexWorker = worker;
    const ready = new Promise<void>((resolve, reject) => {
        rejectIndexBuild = reject;
        worker.onmessage = event => {
            if (id !== loadId)
                return;
            if (event.data.type === 'ready') {
                rejectIndexBuild = null;
                resolve();
            }
            else if (event.data.type === 'result' && event.data.request === indexRequest)
                receiveIndex(event.data.result as IndexResult);
            else if (event.data.type === 'error') {
                if (rejectIndexBuild) {
                    rejectIndexBuild = null;
                    reject(new Error(event.data.message));
                }
                else
                    notify(event.data.message);
            }
        };
        worker.onerror = () => { if (rejectIndexBuild) {
            rejectIndexBuild = null;
            reject(new Error(tr("构件索引进程中断。")));
        }
        else if (id === loadId)
            notify(tr("构件索引进程中断，请重新打开模型。")); };
        worker.postMessage({ type: 'build', records });
    });
    return { ready };
}
function renderAudit() {
    const categories = $('category-summary');
    categories.replaceChildren();
    const heavy = $('heavy-components');
    heavy.replaceChildren();
    if (!model || !indexResult)
        return;
    const faces = indexResult.categories.reduce((count, category) => count + category.triangles, 0);
    const stats = viewer.getStats();
    $('audit-summary').textContent = tr("当前筛选：{0} 个构件，{1} 个三角面（含重复实例）。渲染数组 {2} MiB，复用实例 {3} 个。", { "0": allFiltered.length.toLocaleString(locale()), "1": faces.toLocaleString(locale()), "2": (stats.renderBufferBytes / 1048576).toFixed(1), "3": stats.instancedPlacements.toLocaleString(locale()) });
    const maximum = Math.max(1, ...indexResult.categories.map(category => category.count));
    for (const category of indexResult.categories) {
        const button = document.createElement('button');
        button.className = 'category-stat';
        button.append(textElement('span', category.name || tr("未分类")), textElement('strong', category.count.toLocaleString(locale())), textElement('small', tr("{0} 面", { "0": category.triangles.toLocaleString(locale()) })));
        const bar = document.createElement('i');
        bar.style.width = `${category.count / maximum * 100}%`;
        button.append(bar);
        button.onclick = () => { $<HTMLSelectElement>('category-filter').value = category.name || UNCLASSIFIED_CATEGORY; filterList(); notify(tr("已按类别筛选：{0}", { "0": category.name || tr("未分类") })); };
        categories.append(button);
    }
    for (const item of indexResult.heavy) {
        const component = model.components[item.index]!;
        const button = document.createElement('button');
        button.className = 'heavy-component';
        button.append(textElement('span', component.name || component.id), textElement('small', tr("{0} 面 · {1}", { "0": item.triangles.toLocaleString(locale()), "1": component.id })));
        button.onclick = () => { select(component.id); viewer.focus(component.id); };
        heavy.append(button);
    }
    if (!indexResult.heavy.length)
        heavy.append(textElement('p', tr("当前筛选没有可统计的三角面。"), 'tool-note'));
}
function refreshSectionInputs(updateInputs = true) {
    const box = viewer.getSectionBox();
    if (updateInputs) for (const [axisIndex, axis] of ['x', 'y', 'z'].entries()) {
        $<HTMLInputElement>(`section-min-${axis}`).value = box ? String(Number(box.min[axisIndex]!.toPrecision(10))) : '';
        $<HTMLInputElement>(`section-max-${axis}`).value = box ? String(Number(box.max[axisIndex]!.toPrecision(10))) : '';
    }
    $('section-status').textContent = box ? tr("剖切已启用；盒外部分隐藏，坐标使用模型原始单位。") : tr("尚未启用剖切；坐标使用模型原始单位。");
}
function renderMeasurement() {
    const measurement = viewer.getMeasurement();
    const source = $<HTMLSelectElement>('source-unit').value;
    const target = $<HTMLSelectElement>('measurement-unit').value;
    const raw = measurement?.distance;
    if (raw === null || raw === undefined) {
        $('measurement-result').textContent = measurement?.points.length ? tr("已选第一点，请选择第二点") : measuring ? tr("请在模型表面选择第一点") : tr("尚未测量");
        return;
    }
    const factors: Record<string, number> = { m: 1, mm: 0.001, ft: 0.3048 };
    const converted = source !== 'unknown' && target !== 'model' ? raw * factors[source]! / factors[target]! : raw;
    const unit = target === 'model' || source === 'unknown' ? tr("模型单位") : target;
    $('measurement-result').textContent = `${converted.toLocaleString(locale(), { maximumFractionDigits: 6 })} ${unit}`;
}
function refreshMeasurementNote() {
    const sourceUnit = model?.units?.length ?? 'unknown';
    $('measurement-note').textContent = sourceUnit === 'unknown'
        ? tr('模型包未声明长度单位。请核对源模型后指定；当前是表面两点直线距离，不含顶点或边吸附。')
        : tr('模型包声明长度单位为 {0}。当前是表面两点直线距离，不含顶点或边吸附。', { '0': sourceUnit });
}
function renderFilterOptions() {
    if (!model) return;
    const levels = $<HTMLSelectElement>('level-filter');
    const levelValue = levels.value;
    levels.replaceChildren(new Option(tr('全部楼层'), ''));
    for (const level of model.levels)
        levels.add(new Option(`${level.name} (${level.componentIds.length.toLocaleString(locale())})`, level.name));
    if ([...levels.options].some(option => option.value === levelValue)) levels.value = levelValue;
    const categories = $<HTMLSelectElement>('category-filter');
    const categoryValue = categories.value;
    categories.replaceChildren(new Option(tr('全部类别'), ''));
    const counts = new Map<string, number>();
    for (const component of model.components) counts.set(component.category, (counts.get(component.category) ?? 0) + 1);
    for (const [category, count] of [...counts].sort((a, b) => a[0].localeCompare(b[0], locale())))
        categories.add(new Option(`${category || tr('未分类')} (${count.toLocaleString(locale())})`, category || UNCLASSIFIED_CATEGORY));
    if ([...categories.options].some(option => option.value === categoryValue)) categories.value = categoryValue;
}
function renderViewpoints() {
    const select = $<HTMLSelectElement>('viewpoint-select');
    const selected = select.value;
    select.replaceChildren();
    for (const view of savedViews)
        select.add(new Option(view.name, view.id));
    if (savedViews.some(view => view.id === selected))
        select.value = selected;
    else if (savedViews.length)
        select.selectedIndex = 0;
    $<HTMLButtonElement>('viewpoint-restore').disabled = !savedViews.length;
    $<HTMLButtonElement>('viewpoint-delete').disabled = !savedViews.length;
    $('viewpoint-note').textContent = savedViews.length ? tr("{0} 个视点保存在本机。将自动恢复相机、选择、显示和剖切。", { "0": savedViews.length }) : tr("尚未保存视点。视点只保存在本机当前应用中。");
}
function saveViewStorage() {
    try {
        localStorage.setItem(viewStorageKey, JSON.stringify(savedViews));
        return true;
    }
    catch {
        notify(tr("视点未保存：本机存储空间不足或不可用。可减少视点数量后重试。"));
        return false;
    }
}
function openTool(tool: NonNullable<typeof activeTool>) {
    const closing = activeTool === tool && !$('tools-panel').hidden;
    if (closing) {
        closeTools();
        return;
    }
    if (tool !== 'measurement' && measuring) {
        measuring = false;
        viewer.setMeasurementMode(false);
    }
    activeTool = closing ? null : tool;
    $('tools-panel').hidden = closing;
    $('tools-panel').classList.toggle('quantity-panel', tool === 'quantity');
    $('tools-panel').classList.toggle('assistant-panel', tool === 'assistant');
    for (const id of toolIds) {
        $(`${id}-controls`).hidden = closing || tool !== id;
        $(`${id}-tool`).setAttribute('aria-pressed', String(!closing && tool === id));
    }
    const titles = { section: tr("剖切盒"), measurement: tr("两点测量"), views: tr("命名视点"), audit: tr("统计与审查"), quantity: tr('构件量表'), navigation: tr('漫游与飞行'), assistant: tr('模型助手') };
    $('tools-title').textContent = titles[tool];
    if (tool === 'section')
        refreshSectionInputs();
    else if (tool === 'measurement')
        renderMeasurement();
    else if (tool === 'views')
        renderViewpoints();
    else if (tool === 'quantity')
        renderQuantities();
    else if (tool === 'navigation')
        refreshNavigation();
    else if (tool === 'assistant')
        refreshAssistant();
    else
        renderAudit();
}
function closeTools() {
    if (measuring) {
        measuring = false;
        viewer.setMeasurementMode(false);
    }
    $('tools-panel').hidden = true;
    activeTool = null;
    $('tools-panel').classList.remove('quantity-panel');
    $('tools-panel').classList.remove('assistant-panel');
    for (const id of toolIds)
        $(`${id}-tool`).setAttribute('aria-pressed', 'false');
}
async function displayModel(next: BimModel, id: number) {
    if (id !== loadId)
        return;
    setProgress(tr("准备三维场景"), 64);
    try {
        const indexBuild = await prepareIndex(next, id);
        if (id !== loadId)
            return;
        await Promise.all([viewer.load(next), indexBuild.ready]);
        if (id !== loadId)
            return;
        model = next;
        componentMap = new Map(next.components.map((c, index) => [c.id, index]));
        selectedId = null;
        selectedIds.clear();
        propertyTab = 'properties';
        collapsedGroups.clear();
        viewStorageKey = modelViewKey(next);
        try {
            savedViews = readSavedViews(localStorage.getItem(viewStorageKey));
        }
        catch {
            savedViews = [];
        }
        const sourceUnit = next.units?.length ?? 'unknown';
        $<HTMLSelectElement>('source-unit').value = sourceUnit;
        viewer.setNavigationUnit(sourceUnit);
        $<HTMLSelectElement>('measurement-unit').value = sourceUnit === 'unknown' ? 'model' : sourceUnit;
        refreshMeasurementNote();
        $('load-error').hidden = true;
        $('component-count').textContent = next.components.length.toLocaleString(locale());
        $<HTMLInputElement>('search').value = '';
        $<HTMLSelectElement>('level-filter').value = '';
        $<HTMLSelectElement>('category-filter').value = '';
        renderFilterOptions();
        $('model-label').textContent = next.name;
        $('model-label').title = next.name;
        loading = false;
        filterList();
        renderProperties();
        updateActions();
        if (next.warnings.length)
            notify(tr("模型已打开，有 {0} 项数据提示，可在右侧模型概览查看。", { "0": next.warnings.length }));
    }
    catch (error) {
        if (id !== loadId)
            return;
        closeModel();
        notify(error instanceof Error ? error.message : tr("三维场景创建失败"));
    }
}
function startArchiveWorker(file: File | ArchiveReference, name: string, id: number, depth = 0) {
    const nextWorker = new Worker(new URL('./model.worker.ts', import.meta.url), { type: 'module' });
    worker = nextWorker;
    nextWorker.onmessage = async (event: MessageEvent) => {
        if (id !== loadId)
            return;
        if (event.data.type === 'progress')
            setProgress(event.data.phase, event.data.progress * 60);
        else if (event.data.type === 'loaded') {
            nextWorker.terminate();
            worker = null;
            void displayModel(event.data.model, id);
        }
        else if (event.data.type === 'error') {
            nextWorker.terminate();
            worker = null;
            reportImportError(event.data.message);
        }
        else if (event.data.type === 'choices') {
            try {
                const choice = await selectArchive(event.data.choices as ArchiveChoice[]);
                if (id !== loadId)
                    return;
                if (!choice) {
                    closeModel();
                    notify(tr("已取消选择模型"));
                    return;
                }
                if (choice.kind === 'rvt')
                    throw new Error(tr("原始 RVT 尚未支持独立读取，请选择已导出的模型 ZIP。"));
                if (choice.kind === 'directory' || !window.localBim) {
                    nextWorker.postMessage({ type: 'choose', index: choice.index });
                    return;
                }
                if (depth >= 4)
                    throw new Error(tr("ZIP 嵌套超过四层，请先解压外层。"));
                nextWorker.terminate();
                worker = null;
                readingArchive = true;
                setProgress(tr("读取包内模型"), 2);
                const extracted = await window.localBim.openArchiveEntry(file, choice);
                if (id !== loadId)
                    return;
                readingArchive = false;
                startArchiveWorker(extracted, extracted.name, id, depth + 1);
            }
            catch (error) {
                if (id !== loadId)
                    return;
                nextWorker.terminate();
                worker = null;
                reportImportError(error instanceof Error ? error.message : tr("包内模型读取失败"));
            }
        }
    };
    nextWorker.onerror = () => {
        if (id !== loadId)
            return;
        nextWorker.terminate();
        worker = null;
        reportImportError(tr("模型处理进程中断。请检查设备可用内存，或关闭其他程序后重试。"));
    };
    nextWorker.postMessage({ file, name });
}
window.localBim?.onProgress(data => { if (readingArchive && loading)
    setProgress(data.phase, data.progress * 55); });
async function openFile(file: File) {
    if (viewerUnavailable) {
        notify(tr("当前设备无法初始化三维窗口，请启用硬件加速后重试。"));
        return;
    }
    if (file.name.toLowerCase().endsWith('.rvt')) {
        reportImportError(tr("原始 RVT 尚未支持独立读取。请选择已导出的模型 ZIP；查看器不会启动 Revit。"), false);
        return;
    }
    if (!file.name.toLowerCase().endsWith('.zip')) {
        reportImportError(tr("请选择已导出的模型 ZIP。"), false);
        return;
    }
    if (file.size > MODEL_LIMITS.compressedBytes) {
        reportImportError(tr("模型文件超过 2 GB（2,147,483,648 字节）。"), false);
        return;
    }
    if (!closeModel())
        return;
    const id = ++loadId;
    loading = true;
    updateActions();
    setProgress(tr("读取模型目录"), 1);
    try {
        startArchiveWorker(file, file.name, id);
    }
    catch (error) {
        if (id === loadId)
            reportImportError(error instanceof Error ? error.message : tr("文件读取失败"));
    }
}
function closeModel() {
    if (chooseArchive)
        finishArchiveChoice(null);
    $('load-error').hidden = true;
    if (readingArchive) {
        window.localBim?.cancelArchiveRead();
        readingArchive = false;
    }
    ++loadId;
    worker?.terminate();
    worker = null;
    loading = false;
    indexWorker?.terminate();
    indexWorker = null;
    rejectIndexBuild?.(new Error(tr("构件索引加载已取消。")));
    rejectIndexBuild = null;
    if (listRenderFrame)
        cancelAnimationFrame(listRenderFrame);
    listRenderFrame = 0;
    indexResult = null;
    filtering = false;
    treeRows = new Int32Array();
    triangleCounts = new Float64Array();
    collapsedGroups.clear();
    model = null;
    componentMap.clear();
    selectedId = null;
    selectedIds.clear();
    allFiltered = [];
    savedViews = [];
    viewStorageKey = '';
    measuring = false;
    $('tools-panel').hidden = true;
    activeTool = null;
    $<HTMLInputElement>('ghost-mode').checked = false;
    quantityPanel.clear();
    for (const tool of toolIds)
        $(`${tool}-tool`).setAttribute('aria-pressed', 'false');
    viewerUnavailable = true;
    viewer?.dispose();
    const available = createViewer();
    $('component-count').textContent = '0';
    $('component-list').replaceChildren(textElement('p', tr("打开模型后，在这里浏览构件和楼层。"), 'panel-empty'));
    $('list-summary').textContent = tr("尚未打开模型");
    $('list-more').hidden = true;
    $('model-label').textContent = tr("未打开模型");
    $('model-stats').textContent = '';
    $('footer-status').textContent = tr("准备就绪");
    renderOverview();
    updateActions();
    if (!available)
        $('footer-status').textContent = tr("无法创建三维窗口");
    return available;
}
function createViewer() {
    try {
        viewer = new ModelViewer($('viewport'), () => { if (!selecting && model)
            syncSelection(); }, (phase, progress) => setProgress(phase, 64 + progress * 36));
        viewer.onSelectionChange = () => { if (!selecting && model)
            syncSelection(); };
        viewer.onMeasurementChange = () => { renderMeasurement(); if (model)
            updateActions(); };
        viewer.onNavigationChange = () => refreshNavigation();
        viewerUnavailable = false;
        updateCanvasLanguage();
        return true;
    }
    catch (error) {
        viewerUnavailable = true;
        $('footer-status').textContent = tr("无法创建三维窗口");
        notify(tr("当前设备无法初始化 WebGL：{0}", { "0": error instanceof Error ? error.message : tr("请检查显卡或浏览器设置") }));
        for (const id of ['open-button', 'welcome-open', 'demo-button'])
            $<HTMLButtonElement>(id).disabled = true;
        return false;
    }
}
createViewer();
const input = $<HTMLInputElement>('file-input');
const chooseFile = () => { if (!loading)
    input.click(); };
input.onchange = () => { const file = input.files?.[0]; input.value = ''; if (file)
    void openFile(file); };
for (const id of ['open-button', 'welcome-open'])
    $(id).onclick = chooseFile;
window.addEventListener('bim-open-file', chooseFile);
$('demo-button').onclick = () => { ++loadId; loading = true; updateActions(); void displayModel(createDemoModel(), loadId); };
$('fit-all').onclick = () => viewer.focus();
$('fit-selection').onclick = () => { if (selectedIds.size)
    viewer.focusSelection(); };
$('hide-selection').onclick = () => { if (selectedIds.size) {
    viewer.setVisible(selectedIds, false);
    syncSelection();
    renderList();
    updateActions();
} };
$('isolate-selection').onclick = () => { if (selectedIds.size) {
    viewer.isolate(selectedIds);
    viewer.focusSelection();
    renderList();
    updateActions();
} };
$('show-all').onclick = () => { viewer.showAll(); renderList(); updateActions(); };
$('clear-selection').onclick = () => select(null);
$('close-model').onclick = closeModel;
$('cancel-load').onclick = () => { closeModel(); notify(tr("加载已取消")); };
$('show-filtered').onclick = () => { viewer.isolate(allFiltered.map((c) => c.id)); renderList(); updateActions(); };
$('more-button').onclick = () => { };
let searchTimer: ReturnType<typeof setTimeout>;
$('search').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(filterList, 120); };
for (const id of ['level-filter', 'category-filter'])
    $(id).onchange = filterList;
$('ghost-mode').onchange = () => viewer.setGhostMode($<HTMLInputElement>('ghost-mode').checked);
for (const tool of toolIds)
    $(`${tool}-tool`).onclick = () => openTool(tool);
$('tools-close').onclick = closeTools;
$('section-selection').onclick = () => {
    if (![...selectedIds].some(id => viewer.getComponentBounds(id) !== null)) {
        notify(tr("当前选择没有可显示几何，请选择三维构件后剖切。"));
        return;
    }
    const padding = Number($<HTMLInputElement>('section-padding').value);
    if (!Number.isFinite(padding) || padding < 0) {
        notify(tr("边距必须为非负有限数值。"));
        return;
    }
    viewer.sectionSelection(padding);
    refreshSectionInputs();
};
$('section-all').onclick = () => { viewer.sectionAll(); refreshSectionInputs(); };
$('section-clear').onclick = () => { viewer.setSectionBox(null); refreshSectionInputs(); };
$('section-apply').onclick = () => {
    const min = ['x', 'y', 'z'].map(axis => Number($<HTMLInputElement>(`section-min-${axis}`).value));
    const max = ['x', 'y', 'z'].map(axis => Number($<HTMLInputElement>(`section-max-${axis}`).value));
    if ([...min, ...max].some(value => !Number.isFinite(value)) || min.some((value, index) => value > max[index]!)) {
        notify(tr("请填写有效边界，各轴最小值不能大于最大值。"));
        return;
    }
    viewer.setSectionBox({ min: min as [
            number,
            number,
            number
        ], max: max as [
            number,
            number,
            number
        ] });
    refreshSectionInputs();
};
$('measurement-start').onclick = () => { measuring = true; viewer.clearMeasurement(); viewer.setMeasurementMode(true); renderMeasurement(); };
$('measurement-stop').onclick = () => { measuring = false; viewer.setMeasurementMode(false); renderMeasurement(); };
$('measurement-clear').onclick = () => { viewer.clearMeasurement(); renderMeasurement(); };
for (const id of ['source-unit', 'measurement-unit'])
    $(id).onchange = () => {
        if (id === 'source-unit') viewer.setNavigationUnit($<HTMLSelectElement>('source-unit').value as 'm' | 'mm' | 'ft' | 'unknown');
        renderMeasurement(); refreshNavigation();
    };
$('navigation-unit').onchange = () => {
    const unit = $<HTMLSelectElement>('navigation-unit').value as 'm' | 'mm' | 'ft' | 'unknown';
    $<HTMLSelectElement>('source-unit').value = unit;
    viewer.setNavigationUnit(unit); renderMeasurement(); refreshNavigation();
};
$('navigation-mode').onchange = () => {
    if (measuring) { measuring = false; viewer.setMeasurementMode(false); renderMeasurement(); }
    viewer.setNavigationMode($<HTMLSelectElement>('navigation-mode').value as 'orbit' | 'walk' | 'fly');
    refreshNavigation();
};
$('navigation-exit').onclick = () => { viewer.setNavigationMode('orbit'); refreshNavigation(); };
$('navigation-place').onclick = () => { viewer.resetWalkPlacement(); refreshNavigation(); };
$('navigation-speed').onchange = () => {
    const speed = Number($<HTMLInputElement>('navigation-speed').value);
    if (!Number.isFinite(speed) || speed < 0.1 || speed > 10) { notify(tr('移动速度需在 0.1 至 10 m/s 之间。')); refreshNavigation(); return; }
    viewer.setNavigationSpeed(speed); refreshNavigation();
};
$('navigation-height').onchange = () => {
    const height = Number($<HTMLInputElement>('navigation-height').value);
    if (!Number.isFinite(height) || height < 0.8 || height > 2.2) { notify(tr('人物眼高需在 0.8 至 2.2 m 之间。')); refreshNavigation(); return; }
    viewer.setNavigationEyeHeight(height); refreshNavigation();
};
$('viewpoint-save').onclick = () => {
    if (!model)
        return;
    if (savedViews.length >= 20) {
        notify(tr("每个模型最多保存 20 个视点，请先删除不需要的视点。"));
        return;
    }
    const name = $<HTMLInputElement>('viewpoint-name').value.trim() || tr("视点 {0}", { "0": savedViews.length + 1 });
    const saved = compactView(name, viewer.captureView(), model.components.map(component => component.id));
    savedViews.push(saved);
    if (!saveViewStorage())
        savedViews.pop();
    renderViewpoints();
    $<HTMLSelectElement>('viewpoint-select').value = saved.id;
};
$('viewpoint-restore').onclick = () => {
    if (!model)
        return;
    const saved = savedViews.find(view => view.id === $<HTMLSelectElement>('viewpoint-select').value);
    if (!saved)
        return;
    try {
        viewer.restoreView(expandView(saved, model.components.map(component => component.id)));
    }
    catch (error) {
        notify(error instanceof Error ? error.message : tr("视点无法恢复。"));
        return;
    }
    $<HTMLInputElement>('ghost-mode').checked = saved.view.ghostMode;
    syncSelection();
    renderList();
    refreshSectionInputs();
    notify(tr("已恢复视点：{0}", { "0": saved.name }));
};
$('viewpoint-delete').onclick = () => {
    const id = $<HTMLSelectElement>('viewpoint-select').value;
    const previous = savedViews;
    savedViews = savedViews.filter(view => view.id !== id);
    if (!saveViewStorage())
        savedViews = previous;
    renderViewpoints();
};
const help = $<HTMLDialogElement>('help-dialog');
$('help-button').onclick = () => help.showModal();
$('close-help').onclick = () => help.close();
help.addEventListener('click', (event) => { if (event.target === help)
    help.close(); });
let dragDepth = 0;
window.addEventListener('dragenter', (event) => { if (event.dataTransfer?.types.includes('Files')) {
    event.preventDefault();
    dragDepth++;
    $('drop-overlay').hidden = false;
} });
window.addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth)
    $('drop-overlay').hidden = true; });
window.addEventListener('dragover', (event) => { if (event.dataTransfer?.types.includes('Files'))
    event.preventDefault(); });
window.addEventListener('drop', (event) => { event.preventDefault(); dragDepth = 0; $('drop-overlay').hidden = true; const file = event.dataTransfer?.files[0]; if (file)
    void openFile(file); });
window.addEventListener('keydown', (event) => {
    if (event.defaultPrevented) return;
    if (viewerUnavailable)
        return;
    if (document.querySelector('dialog[open]') || (event.target instanceof HTMLElement && event.target.closest('input,select,textarea,[contenteditable="true"]')) || event.ctrlKey || event.metaKey || event.altKey)
        return;
    if (loading)
        return;
    if (event.key === 'Escape') {
        if (viewer.getNavigationState().mode !== 'orbit') { viewer.setNavigationMode('orbit'); refreshNavigation(); return; }
        if (measuring) {
            measuring = false;
            viewer.setMeasurementMode(false);
            renderMeasurement();
        }
        else
            select(null);
    }
    else if (model && event.key === 'Home') {
        event.preventDefault();
        viewer.focus();
    }
    else if (selectedId && event.key.toLowerCase() === 'f')
        viewer.focusSelection();
    else if (selectedId && event.key.toLowerCase() === 'h')
        $('hide-selection').click();
    else if (selectedId && event.key.toLowerCase() === 'i')
        $('isolate-selection').click();
});
window.addEventListener('beforeunload', () => { worker?.terminate(); indexWorker?.terminate(); toolbarResizeObserver.disconnect(); assistantPanel.destroy(); viewer?.dispose(); });

function updateCanvasLanguage() {
    $('viewport').querySelector('canvas')?.setAttribute('aria-label', tr('三维模型，拖动旋转，滚轮缩放，单击选择构件'));
}
/** Update labels in place without recreating the renderer or changing model state. */
function applyBrand() {
    const presentation = brandPresentation(getLanguage());
    $('brand-name').textContent = presentation.name;
    $('brand-subtitle').textContent = presentation.subtitle;
    $('company-attribution').textContent = presentation.copyright;
    $('version-label').textContent = tr('预览版 {0}', { '0': brand.version });
    $('brand-license-heading').textContent = presentation.licenseHeading;
    $('brand-attribution').textContent = presentation.copyright;
    $('brand-license-summary').textContent = presentation.licenseSummary;
    document.title = presentation.title;
}

function applyLanguage() {
    const language = getLanguage();
    document.documentElement.lang = language === 'en' ? 'en' : 'zh-CN';
    applyStaticTranslations();
    $('language-toggle').textContent = language === 'en' ? '中文' : 'English';
    applyBrand();
    updateCanvasLanguage();
    refreshArchiveLabels();
    const properties = $('properties');
    const scrollTop = properties.scrollTop;
    const expandedGroups = [...properties.querySelectorAll<HTMLDetailsElement>('details')].map(group => group.open);
    renderProperties();
    [...properties.querySelectorAll<HTMLDetailsElement>('details')].forEach((group, index) => { if (expandedGroups[index] !== undefined) group.open = expandedGroups[index]!; });
    properties.scrollTop = scrollTop;
    if (model) {
        $('component-count').textContent = model.components.length.toLocaleString(locale());
        renderFilterOptions();
        $('list-summary').textContent = filtering ? tr('正在筛选…') : tr('{0} 个构件', { '0': allFiltered.length.toLocaleString(locale()) });
        $('show-filtered').setAttribute('aria-label', tr('隔离筛选得到的 {0} 个构件', { '0': allFiltered.length }));
        renderList();
        renderAudit();
        renderQuantities();
    } else {
        $('list-summary').textContent = tr('尚未打开模型');
        $('component-list').replaceChildren(textElement('p', tr('打开模型后，在这里浏览构件和楼层。'), 'panel-empty'));
        $<HTMLSelectElement>('level-filter').replaceChildren(new Option(tr('全部楼层'), ''));
        $<HTMLSelectElement>('category-filter').replaceChildren(new Option(tr('全部类别'), ''));
        $('model-label').textContent = tr('未打开模型');
        $('footer-status').textContent = tr(viewerUnavailable ? '无法创建三维窗口' : '准备就绪');
    }
    renderViewpoints();
    refreshMeasurementNote();
    if (!viewerUnavailable) {
        refreshSectionInputs(false);
        renderMeasurement();
    }
    if (activeTool) {
        const titles = { section: '剖切盒', measurement: '两点测量', views: '命名视点', audit: '统计与审查', quantity: '构件量表', navigation: '漫游与飞行', assistant: '模型助手' };
        $('tools-title').textContent = tr(titles[activeTool]);
    }
    $('load-phase').textContent = translatedProgress(currentPhase);
    if (!$('toast').hidden) $('toast').textContent = translatedMessage(lastToastMessage);
    if (!$('load-error').hidden) $('load-error-message').textContent = translatedMessage(lastImportError, true);
    updateActions();
    window.dispatchEvent(new CustomEvent('bright-language-changed', { detail: { language } }));
}
$('language-toggle').onclick = () => {
    setLanguage(getLanguage() === 'en' ? 'zh' : 'en');
    applyLanguage();
    void window.desktopApp?.setLanguage(getLanguage()).catch(() => { /* The web UI remains usable if native preferences are unavailable. */ });
};
applyLanguage();
void window.desktopApp?.setLanguage(getLanguage()).catch(() => { /* Synchronize a persisted web preference with the native menu when available. */ });
