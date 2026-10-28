const { spawn } = require('node:child_process');
const path = require('node:path');
const { app, ipcMain, dialog } = require('electron');

const EMPTY = { status: 'ready', exporterVersion: '1.7.0', hosts: [] };
const STATES = new Set(['not-needed', 'ready', 'installed', 'deferred', 'failed']);

function normalizeResult(value) {
  if (!value || typeof value !== 'object' || !STATES.has(value.status) || !Array.isArray(value.hosts)) throw new Error('Invalid integration response.');
  return {
    status: value.status,
    exporterVersion: typeof value.exporterVersion === 'string' ? value.exporterVersion.slice(0, 40) : '1.7.0',
    hosts: value.hosts.slice(0, 24).map(host => {
      if (!host || !/^(201[5-9]|202[0-6])$/.test(String(host.year)) || !['installed', 'ready', 'unsupported'].includes(host.status)) throw new Error('Invalid host response.');
      return { year: String(host.year), runtime: String(host.runtime || 'unknown').slice(0, 40), managed: host.managed === true, status: host.status,
        ...(typeof host.managedVersion === 'string' ? { managedVersion: host.managedVersion.slice(0, 40) } : {}) };
    }),
    ...(typeof value.message === 'string' ? { message: value.message.slice(0, 8192) } : {}),
    ...(typeof value.code === 'string' ? { code: value.code.slice(0, 100) } : {}),
  };
}

function runIntegration(action, { script, modulePath, logPath }, spawnProcess = spawn) {
  if (!['Status', 'Install', 'Uninstall'].includes(action)) throw new Error('Invalid integration action.');
  const executable = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const args = ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', script,
    '-Action', action, '-ModulePath', modulePath, '-InstallContext', 'CurrentUser'];
  if (action !== 'Status') args.push('-LogPath', logPath);
  return new Promise((resolve, reject) => {
    let stdout = '', stderr = '', settled = false, oversized = false, outputBytes = 0;
    const readOnly = action === 'Status';
    const child = spawnProcess(executable, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    const finish = (error, result) => {
      if (settled) return; settled = true; clearTimeout(timer);
      if (error) reject(error); else resolve(result);
    };
    const timer = readOnly ? setTimeout(() => { child.kill(); finish(new Error('Integration timed out.')); }, 60000) : null;
    child.once('error', error => finish(error));
    child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      outputBytes += Buffer.byteLength(chunk, 'utf8');
      if (outputBytes > 1024 * 1024) {
        oversized = true;
        if (readOnly) { child.kill(); finish(new Error('Integration output exceeded limit.')); }
      } else if (!oversized) stdout += chunk;
    });
    child.stderr.on('data', chunk => { if (stderr.length < 8192) stderr += chunk.toString('utf8'); });
    child.once('close', code => {
      try {
        if (oversized) throw new Error('Integration output exceeded limit.');
        if (code !== 0) throw new Error(stderr || 'Integration could not run.');
        finish(null, normalizeResult(JSON.parse(stdout.replace(/^\uFEFF/, '').trim())));
      } catch (error) { finish(error); }
    });
  });
}

function setupRevitIntegration({ getWindow, trusted, getLanguage }) {
  const project = app.getAppPath();
  const resources = app.isPackaged ? process.resourcesPath : path.join(project, 'installer/generated');
  const paths = {
    modulePath: path.join(resources, 'revit-plugin'),
    script: app.isPackaged ? path.join(resources, 'revit-integration/Install-RevitIntegration.ps1') : path.join(project, 'installer/Install-RevitIntegration.ps1'),
    logPath: path.join(app.getPath('userData'), 'revit-integration.json'),
  };
  let state = { ...EMPTY }, busy = false;
  const publish = () => {
    const window = getWindow();
    if (window && !window.isDestroyed()) window.webContents.send('bim:revit-state', state);
  };
  const perform = async action => {
    if (busy) return state;
    busy = true; state = { ...state, status: 'busy' }; publish();
    try { state = await runIntegration(action, paths); }
    catch { state = { ...state, status: 'failed', code: 'integration-unavailable', message: getLanguage() === 'en' ? 'The bundled Revit integration could not run. Check the installation or try again.' : '无法运行内置 Revit 集成组件，请检查安装或重试。' }; }
    finally { busy = false; publish(); }
    return state;
  };
  const guard = event => { if (!trusted(event)) throw new Error('Untrusted application frame.'); };
  ipcMain.handle('bim:revit-status', event => { guard(event); return perform('Status'); });
  ipcMain.handle('bim:revit-install', event => { guard(event); return perform('Install'); });
  ipcMain.handle('bim:revit-uninstall', async event => {
    guard(event); if (busy) return state;
    // Hold the operation lock across the native confirmation as well as IO.
    busy = true;
    const previous = state;
    state = { ...state, status: 'busy' }; publish();
    let confirmed = false;
    try {
      const en = getLanguage() === 'en';
      const result = await dialog.showMessageBox(getWindow(), {
        type: 'question', title: en ? 'Remove Revit export plugin' : '卸载 Revit 导出插件',
        message: en ? 'Remove the export plugin for this Windows user?' : '卸载当前 Windows 用户的 Revit 导出插件？',
        detail: en ? 'The model viewer and exported ZIP files remain available. Close Revit first. Verified old registrations will be restored.' : '查看器和已导出的 ZIP 仍可使用。请先关闭 Revit，已核验的旧注册会恢复。',
        buttons: en ? ['Remove plugin', 'Cancel'] : ['卸载插件', '取消'], defaultId: 1, cancelId: 1, noLink: true,
      });
      confirmed = result.response === 0;
    } catch {
      state = { ...previous, status: 'failed', code: 'confirmation-unavailable' };
    } finally {
      busy = false;
      if (state.status === 'busy') state = previous;
      publish();
    }
    return confirmed ? perform('Uninstall') : state;
  });
  return { perform };
}
module.exports = { setupRevitIntegration, runIntegration, normalizeResult };
