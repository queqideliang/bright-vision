const { app, ipcMain, dialog } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const { createUpdateService } = require('./update-service.cjs');
const { createGitHubUpdateSource } = require('./github-update-source.cjs');

// Model data never enters the update channel. The user can select a local
// directory or the fixed official GitHub identity shipped in the application.
// Renderer input never supplies a remote URL, account, token or command.
async function setupDesktopUpdates({ getWindow, trusted, onLanguage, officialReleaseConfig = require('./update-release.json'), githubRequest }) {
  const settingsPath = path.join(app.getPath('userData'), 'desktop-settings.json');
  let settings = {};
  try {
    const stat = await fs.lstat(settingsPath);
    if (stat.isFile() && !stat.isSymbolicLink() && stat.size <= 8192) {
      const value = JSON.parse(await fs.readFile(settingsPath, 'utf8'));
      if (value && typeof value === 'object') {
        if (value.language === 'en' || value.language === 'zh') settings.language = value.language;
        if (typeof value.feedDirectory === 'string' && value.feedDirectory.length <= 4096 && path.isAbsolute(value.feedDirectory)) settings.feedDirectory = value.feedDirectory;
        if (value.sourceKind === 'local' || value.sourceKind === 'github') settings.sourceKind = value.sourceKind;
      }
    }
  } catch { /* A missing or invalid preference file uses local defaults. */ }
  const save = async () => {
    await fs.mkdir(path.dirname(settingsPath), { recursive: true });
    const temp = `${settingsPath}.${randomUUID()}.tmp`;
    try { await fs.writeFile(temp, JSON.stringify(settings), { flag: 'wx' }); await fs.rename(temp, settingsPath); }
    finally { await fs.unlink(temp).catch(() => {}); }
  };
  const localService = createUpdateService({ currentVersion: app.getVersion(), userDataPath: app.getPath('userData') });
  let githubService, officialConfigured = false;
  try {
    const config = officialReleaseConfig;
    if (!config || config.schemaVersion !== 1 || typeof config.enabled !== 'boolean' || config.productId !== 'local.bim.modelviewer' || !['preview', 'stable'].includes(config.channel)
      || Object.keys(config).some(key => !['schemaVersion', 'enabled', 'owner', 'repository', 'channel', 'productId'].includes(key))) throw new Error('Invalid official release build configuration.');
    githubService = createGitHubUpdateSource({ ...(config.enabled ? { owner: config.owner, repository: config.repository } : {}), channel: config.channel,
      currentVersion: app.getVersion(), userDataPath: app.getPath('userData'), ...(githubRequest ? { request: githubRequest } : {}) });
    officialConfigured = config.enabled && !!githubService.getState().repositoryDisplay;
  } catch {
    // A bad build configuration must not crash the local viewer or trigger a
    // request to an unknown endpoint. Source and release remain unconfigured.
    githubService = createGitHubUpdateSource({ currentVersion: app.getVersion(), userDataPath: app.getPath('userData') });
  }
  if (!settings.sourceKind && settings.feedDirectory) settings.sourceKind = 'local';
  const activeService = () => settings.sourceKind === 'local' ? localService : githubService;
  let state = activeService().getState();
  let busy = false;
  const publicState = () => {
    const { stagedPath, ...safe } = state;
    const sourceKind = settings.sourceKind === 'local' ? 'local' : 'github';
    return { ...safe, sourceKind, officialConfigured, source: sourceKind === 'local' ? settings.feedDirectory || '' : githubService.getState().sourceUrl, busy };
  };
  const notify = () => {
    const window = getWindow();
    if (window && !window.isDestroyed()) window.webContents.send('bim:update-state', publicState());
  };
  async function discoverSource() {
    if (settings.feedDirectory) return settings.feedDirectory;
    // Portable distributions can carry a sibling updates folder. Installed
    // copies can also find the same folder next to the installation directory.
    const executableDirectory = path.dirname(app.getPath('exe'));
    for (const directory of [path.join(executableDirectory, 'updates'), path.join(path.dirname(executableDirectory), 'updates')]) {
      try {
        const stat = await fs.lstat(path.join(directory, 'update.json'));
        if (stat.isFile() && !stat.isSymbolicLink()) { settings.feedDirectory = directory; return directory; }
      } catch { /* No bundled local source. */ }
    }
    return undefined;
  }
  async function check() {
    if (busy) return publicState();
    busy = true; notify();
    try {
      // Existing portable/local installs retain their discovered sibling feed.
      if (!settings.sourceKind) settings.sourceKind = await discoverSource() ? 'local' : 'github';
      state = settings.sourceKind === 'local' ? await localService.check(await discoverSource()) : await githubService.check();
    }
    catch { state = { status: 'error', currentVersion: app.getVersion(), code: 'CHECK_FAILED' }; }
    finally { busy = false; notify(); }
    return publicState();
  }
  const guard = event => { if (!trusted(event)) throw new Error('Untrusted application frame.'); };
  ipcMain.handle('bim:desktop-info', event => { guard(event); return { version: app.getVersion(), language: settings.language || 'zh', update: publicState() }; });
  ipcMain.handle('bim:desktop-language', async (event, language) => {
    guard(event);
    if (language !== 'zh' && language !== 'en') throw new Error('Unsupported language.');
    settings.language = language; onLanguage(language);
    await save(); return language;
  });
  ipcMain.handle('bim:update-check', event => { guard(event); return check(); });
  ipcMain.handle('bim:update-source', async event => {
    guard(event); if (busy) return publicState();
    busy = true; notify();
    try {
      const en = settings.language === 'en';
      const result = await dialog.showOpenDialog(getWindow(), {
        title: en ? 'Select a trusted update folder containing update.json' : '选择可信更新目录（包含 update.json）',
        buttonLabel: en ? 'Use this folder' : '使用此目录', properties: ['openDirectory'],
      });
      if (!result.canceled && result.filePaths.length === 1) { settings.feedDirectory = result.filePaths[0]; settings.sourceKind = 'local'; await save(); }
    } finally { busy = false; notify(); }
    return check();
  });
  ipcMain.handle('bim:update-official', async event => {
    guard(event); if (busy) return publicState();
    settings.sourceKind = 'github'; await save(); return check();
  });
  ipcMain.handle('bim:update-install', async event => {
    guard(event);
    if (busy || state.status !== 'update-available') return publicState();
    busy = true; notify();
    try {
      const service = activeService();
      state = await service.stage(); notify();
      if (state.status === 'staged') {
        const en = settings.language === 'en';
        const result = await dialog.showMessageBox(getWindow(), {
          type: 'question', title: en ? 'Install update' : '安装更新',
          message: en ? `Install version ${state.latestVersion}?` : `安装 ${state.latestVersion} 版本？`,
          detail: en
            ? 'The viewer will close and the verified installer will open. Reopen the viewer after installation. SHA-256 verifies byte integrity, not publisher identity. Official GitHub releases are restricted to the configured company repository.'
            : '查看器将关闭并打开已校验的安装程序，安装完成后可重新打开。SHA-256 确认字节完整性，不能证明发布者身份。官方 GitHub 更新仅使用程序配置的公司仓库。',
          buttons: en ? ['Install and close viewer', 'Cancel'] : ['安装并关闭查看器', '取消'],
          defaultId: 1, cancelId: 1, noLink: true,
        });
        if (result.response !== 0) {
          // Keep the verified copy so repeated cancellation does not copy again.
          const { stagedPath, ...available } = state;
          state = { ...available, status: 'update-available' };
        } else {
          state = await service.verifyStaged();
          if (state.status === 'staged') {
            const installer = state.stagedPath;
            await new Promise((resolve, reject) => {
              // Launch only the verified private copy, with no command shell.
              // NSIS discovers the existing registered installation directory.
              const child = spawn(installer, [], { detached: true, stdio: 'ignore', windowsHide: false });
              child.once('error', reject); child.once('spawn', () => { child.unref(); resolve(); });
            });
            app.quit();
          }
        }
      }
    } catch { state = { status: 'error', currentVersion: app.getVersion(), code: 'INSTALL_FAILED' }; }
    finally { busy = false; notify(); }
    return publicState();
  });
  onLanguage(settings.language || 'zh');
  const interval = setInterval(() => { void check(); }, 4 * 60 * 60 * 1000); interval.unref();
  app.once('before-quit', () => { clearInterval(interval); githubService.cancel(); });
  return { check, publicState };
}
module.exports = { setupDesktopUpdates };
