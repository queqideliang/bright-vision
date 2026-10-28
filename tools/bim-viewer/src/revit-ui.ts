import type { DesktopRevitState } from './desktop.d.ts';
import { tr } from './i18n.ts';
import './revit-ui.css';

export function mountRevitUI() {
  const button = document.getElementById('revit-button');
  if (!button) return;
  const dialog = document.createElement('dialog');
  dialog.id = 'revit-dialog';
  dialog.setAttribute('aria-labelledby', 'revit-title');
  dialog.innerHTML = `<div class="dialog-heading"><h2 id="revit-title"></h2><button id="revit-close" class="icon-button">×</button></div><div class="revit-content"><p id="revit-version" class="revit-version"></p><p id="revit-status" role="status" aria-live="polite"></p><p id="revit-optional"></p><div id="revit-host-list"><table class="revit-host-table"><thead><tr><th id="revit-host-heading" scope="col"></th><th id="revit-registration-heading" scope="col"></th></tr></thead><tbody id="revit-hosts"></tbody></table></div><p id="revit-host-empty"></p><p id="revit-included" class="revit-note"></p><p id="revit-instructions" class="revit-note"></p><details id="revit-diagnostic" hidden><summary id="revit-diagnostic-heading"></summary><pre id="revit-diagnostic-text"></pre></details><div class="revit-actions"><button id="revit-refresh" class="quiet"></button><button id="revit-install" class="primary"></button><button id="revit-uninstall" class="quiet"></button></div></div>`;
  document.body.append(dialog);
  const element = (id: string) => document.getElementById(id)!;
  const refresh = element('revit-refresh') as HTMLButtonElement;
  const install = element('revit-install') as HTMLButtonElement;
  const uninstall = element('revit-uninstall') as HTMLButtonElement;
  const desktop = window.desktopApp;
  const available = typeof desktop?.getRevitStatus === 'function';
  let state: DesktopRevitState = { status: 'busy', exporterVersion: '1.7.0', hosts: [] };
  let pending = false;
  let checking = available;

  function render() {
    const busy = pending || state.status === 'busy';
    element('revit-title').textContent = tr('Revit插件');
    element('revit-close').setAttribute('aria-label', tr('关闭Revit插件管理'));
    element('revit-version').textContent = tr('随应用提供的导出插件：{0}', { '0': state.exporterVersion || '1.7.0' });
    const statusMessages = {
      'not-needed': '未检测到 Revit。仍可直接打开导出的模型 ZIP，无需安装 Revit。',
      ready: '已检测到 Revit，可安装或修复导出插件。',
      installed: '导出插件已注册。重启 Revit，在 BV → bright vision 面板打开“土建原始量工作台”或“本地导出模型 ZIP”。',
      deferred: 'Revit 正在运行。请保存模型并关闭全部 Revit 窗口，再点“重新检测”。',
      failed: '插件操作未完成。请重新检测后重试，或查看详情。',
      busy: '正在处理 Revit 插件，请稍候…',
    };
    element('revit-status').textContent = !available
      ? tr('网页版不提供本地插件安装。请使用 Windows 桌面版管理 Revit 插件。')
      : checking ? tr('正在检测 Revit 与插件注册状态…')
      : pending ? tr('正在处理 Revit 插件，请稍候…')
      : tr(statusMessages[state.status]);
    element('revit-status').dataset.status = available ? state.status : 'web';
    element('revit-optional').textContent = tr('没有 Revit 也能直接查看已导出的模型 ZIP。');
    element('revit-host-heading').textContent = tr('检测到的 Revit');
    element('revit-registration-heading').textContent = tr('插件注册');
    const hosts = element('revit-hosts');
    hosts.replaceChildren();
    for (const host of state.hosts) {
      const row = document.createElement('tr');
      row.dataset.year = host.year;
      row.dataset.managed = String(host.managed);
      const version = document.createElement('td');
      const year = document.createElement('strong');
      year.textContent = `Revit ${host.year}`;
      version.append(year);
      if (host.runtime) {
        const runtime = document.createElement('small');
        const runtimes: Record<string, string> = { net45: '.NET Framework 4.5', net48: '.NET Framework 4.8', netframework: '.NET Framework', net8: '.NET 8', net10: '.NET 10' };
        runtime.textContent = runtimes[host.runtime] ?? tr('运行环境待确认');
        version.append(runtime);
      }
      const registration = document.createElement('td');
      registration.textContent = host.status === 'unsupported' ? tr('暂不支持此运行环境') : host.managed ? tr('已注册') : tr('未注册');
      if (host.managedVersion) {
        const managedVersion = document.createElement('small');
        managedVersion.textContent = host.managedVersion;
        registration.append(managedVersion);
      }
      row.append(version, registration);
      hosts.append(row);
    }
    element('revit-host-list').hidden = !available || !state.hosts.length;
    element('revit-host-empty').hidden = !available || !!state.hosts.length || checking || busy;
    element('revit-host-empty').textContent = tr('未找到可管理的 Revit 版本。');
    element('revit-included').textContent = tr('导出插件已包含在本应用中，无需另行下载安装包。安装新的 Revit 后，也可在这里补注册。');
    element('revit-instructions').textContent = tr('安装或修复前请保存模型并关闭全部 Revit 窗口。卸载插件会保留本工作台，并按安装记录恢复原有导出入口。');
    element('revit-included').hidden = !available;
    element('revit-instructions').hidden = !available;
    element('revit-diagnostic-heading').textContent = tr('查看详情（原始诊断）');
    element('revit-diagnostic').hidden = state.status !== 'failed' || (!state.message && !state.code);
    element('revit-diagnostic-text').textContent = [state.code, state.message].filter(Boolean).join('\n');
    refresh.textContent = tr('重新检测');
    install.textContent = tr('安装 / 修复插件');
    uninstall.textContent = tr('卸载插件');
    refresh.disabled = !available || busy || checking;
    const supported = state.hosts.some(host => host.status !== 'unsupported');
    install.disabled = !available || busy || checking || state.status === 'not-needed' || state.status === 'deferred' || !supported;
    uninstall.disabled = !available || busy || checking || state.status === 'deferred' || !state.hosts.some(host => host.managed);
    button!.dataset.status = available ? state.status : 'web';
  }

  async function act(operation: () => Promise<DesktopRevitState>, detect = false) {
    if (pending) return;
    pending = true;
    checking = detect;
    render();
    try { state = await operation(); }
    catch (error) {
      state = { ...state, status: 'failed', code: 'revit-service-unavailable', message: error instanceof Error ? error.message : String(error) };
    } finally {
      pending = false;
      checking = false;
      render();
    }
  }
  function open() {
    render();
    if (!dialog.open) dialog.showModal();
    if (available && !pending && state.status !== 'busy') void act(() => desktop!.getRevitStatus(), true);
  }
  button.addEventListener('click', open);
  element('revit-close').addEventListener('click', () => dialog.close());
  document.getElementById('help-revit-button')?.addEventListener('click', event => {
    (event.currentTarget as HTMLElement).closest('dialog')?.close();
    open();
  });
  refresh.addEventListener('click', () => { if (available) void act(() => desktop!.getRevitStatus(), true); });
  install.addEventListener('click', () => { if (available) void act(() => desktop!.installRevitPlugin()); });
  uninstall.addEventListener('click', () => { if (available) void act(() => desktop!.uninstallRevitPlugin()); });
  window.addEventListener('bim-open-revit', open);
  window.addEventListener('bright-language-changed', render);
  if (available) {
    desktop!.onRevitStatus(value => { state = value; render(); });
    void act(() => desktop!.getRevitStatus(), true);
  }
  render();
}
