import type { DesktopUpdateState } from './desktop.d.ts';
import './update-ui.css';
import { brand } from './brand.ts';

export function mountUpdateUI() {
  const button = document.getElementById('update-button');
  if (!button) return;
  const dialog = document.createElement('dialog');
  dialog.id = 'update-dialog';
  dialog.setAttribute('aria-labelledby', 'update-title');
  dialog.innerHTML = `<div class="dialog-heading"><h2 id="update-title"></h2><button id="update-close" class="icon-button" aria-label="Close">×</button></div><div class="help-content"><p id="update-current"></p><p id="update-status" role="status" aria-live="polite"></p><p id="update-source-label"></p><p id="update-source-path" class="update-source-path"></p><p id="update-note"></p><div class="update-actions"><button id="update-check" class="quiet"></button><button id="update-official" class="quiet"></button><button id="update-source" class="quiet"></button><button id="update-install" class="primary" hidden></button></div></div>`;
  document.body.append(dialog);
  const element = (id: string) => document.getElementById(id)!;
  const check = element('update-check') as HTMLButtonElement;
  const source = element('update-source') as HTMLButtonElement;
  const official = element('update-official') as HTMLButtonElement;
  const install = element('update-install') as HTMLButtonElement;
  let state: DesktopUpdateState = { status: 'not-configured', currentVersion: brand.version };
  let failed = false;
  function render() {
    const en = document.documentElement.lang === 'en';
    const desktop = !!window.desktopApp;
    const github = state.sourceKind === 'github';
    element('update-title').textContent = en ? 'Update' : '更新';
    element('update-close').setAttribute('aria-label', en ? 'Close updates' : '关闭更新');
    element('update-current').textContent = (en ? 'Current version: ' : '当前版本：') + (state.currentVersion || '—');
    const statuses: Record<string, string> = en ? {
      'not-configured': github ? 'The official GitHub repository has not been configured in this build. No online request is made; you can use a trusted local update folder.' : 'Select a trusted local or shared update folder to enable automatic checks.',
      'up-to-date': github ? 'You have the latest visible version in this official GitHub channel.' : 'You have the latest version published in this update folder.',
      'update-available': `Version ${state.latestVersion || ''} is available.`,
      'staged': `Version ${state.latestVersion || ''} has been verified.`,
      'error': github ? 'Could not verify the official GitHub update. Check connectivity and published release files, then retry.' : 'Could not verify the update. Check the folder, manifest and installer, then try again.',
    } : {
      'not-configured': github ? '本版本尚未配置官方 GitHub 仓库，不会发起线上请求；仍可选择可信的本地更新目录。' : '选择可信的本地或共享更新目录，即可启用自动检查。',
      'up-to-date': github ? '当前已是此官方 GitHub 渠道最新的可见版本。' : '当前已是此更新目录发布的最新版本。',
      'update-available': `发现新版本 ${state.latestVersion || ''}。`,
      'staged': `新版本 ${state.latestVersion || ''} 已校验。`,
      'error': github ? '无法验证官方 GitHub 更新，请检查网络和发布文件后重试。' : '无法验证更新，请检查目录、更新清单和安装包后重试。',
    };
    if (github && state.code === 'no-compatible-release') statuses.error = en
      ? 'This official GitHub channel has no compatible public release yet.'
      : '此官方 GitHub 渠道尚无兼容的公开版本。';
    if (github && state.code === 'release-not-found') statuses.error = en
      ? 'The official repository or its release files are not public yet.'
      : '官方仓库或其发布文件尚未公开。';
    element('update-status').textContent = !desktop
      ? (en ? 'Updates are available in the Windows desktop app. For the web version, deploy a new web package and refresh this page.' : '更新功能用于 Windows 桌面版；网页版请部署新版网页包后刷新。')
      : failed ? (en ? 'The update service is unavailable. Please try again.' : '更新服务暂不可用，请重试。')
      : state.busy ? (en ? 'Checking and verifying update files…' : '正在检查和校验更新文件…') : statuses[state.status] || statuses.error;
    element('update-source-label').textContent = github ? (en ? 'Official GitHub release channel' : '官方 GitHub 发布渠道') : (en ? 'Update folder' : '更新目录');
    element('update-source-path').textContent = state.source || (github ? (en ? 'Official repository not configured' : '官方仓库尚未配置') : (en ? 'Not selected' : '尚未选择'));
    element('update-note').textContent = github && state.officialConfigured
      ? (en ? 'Only public release metadata and installers from the configured company repository are requested, at startup and every four hours. Verified installation requires confirmation. Model files stay on your device.' : '启动和每四小时仅向程序配置的公司仓库请求公开发布信息与安装包，校验后由你确认安装。模型文件保留在本机。')
      : (en ? 'Local update folders remain supported. Online updates require a configured official repository; an unconfigured source makes no requests. Installation requires confirmation. Model files stay on your device.' : '继续支持本地更新目录。线上更新需要配置官方仓库；未配置时不联网。安装前由你确认，模型文件保留在本机。');
    check.textContent = en ? 'Check for updates' : '检查更新';
    source.textContent = en ? 'Select update folder' : '选择更新目录';
    official.textContent = en ? 'Use official GitHub' : '使用官方 GitHub';
    install.textContent = en ? 'Install update…' : '安装更新…';
    check.disabled = source.disabled = official.disabled = !desktop || !!state.busy;
    install.hidden = !desktop || state.status !== 'update-available'; install.disabled = !!state.busy;
    button!.classList.toggle('has-update', state.status === 'update-available');
    button!.title = state.status === 'update-available' ? (en ? `Version ${state.latestVersion} available` : `发现新版本 ${state.latestVersion}`) : (en ? 'Update' : '更新');
  }
  async function act(operation: (() => Promise<DesktopUpdateState>) | undefined) {
    if (!operation) return;
    state = { ...state, busy: true }; failed = false; render();
    try { state = await operation(); } catch { failed = true; state = { ...state, busy: false }; }
    render();
  }
  const open = () => { render(); if (!dialog.open) dialog.showModal(); };
  button.addEventListener('click', open);
  element('update-close').addEventListener('click', () => dialog.close());
  window.addEventListener('bim-open-updates', open);
  window.addEventListener('bright-language-changed', render);
  check.addEventListener('click', () => void act(window.desktopApp && (() => window.desktopApp!.checkUpdates())));
  source.addEventListener('click', () => void act(window.desktopApp && (() => window.desktopApp!.chooseUpdateSource())));
  official.addEventListener('click', () => void act(window.desktopApp && (() => window.desktopApp!.useOfficialUpdates())));
  install.addEventListener('click', () => void act(window.desktopApp && (() => window.desktopApp!.installUpdate())));
  window.desktopApp?.onUpdate(value => { state = value; failed = false; render(); });
  void window.desktopApp?.getInfo().then(info => { state = info.update; render(); }).catch(() => { failed = true; render(); });
  render();
}
