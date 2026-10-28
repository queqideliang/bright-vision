const { app, BrowserWindow, Menu, dialog, protocol, session, ipcMain } = require('electron');
const { readFile } = require('node:fs/promises');
const { createReadStream, mkdirSync } = require('node:fs');
const { Readable } = require('node:stream');
const path = require('node:path');
const { resolveAssetPath, responseHeaders } = require('./static-assets.cjs');
const { extractArchiveEntry } = require('./archive-extractor.cjs');
const { setupDesktopUpdates } = require('./desktop-updates.cjs');
const { setupRevitIntegration } = require('./revit-integration.cjs');

// Keep the existing profile when the visible product name changes.
app.setName('bright.vision');
const profilePath = process.argv.includes('--test-hidden') && process.env.BIM_TEST_USER_DATA && path.isAbsolute(process.env.BIM_TEST_USER_DATA)
  ? process.env.BIM_TEST_USER_DATA : path.join(app.getPath('appData'), 'bim-model-viewer');
mkdirSync(profilePath, { recursive: true });
app.setPath('userData', profilePath);
app.setPath('sessionData', profilePath);
if (process.platform === 'win32') app.setAppUserModelId('local.bim.modelviewer');

const VIEWER_URL = 'app://viewer/';
let mainWindow = null;
let archiveRead = null;
const archiveModels = new Map();
let uiLanguage = 'zh';
const label = (zh, en) => uiLanguage === 'en' ? en : zh;

protocol.registerSchemesAsPrivileged([
  { scheme: 'app', privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } },
]);

function openFilePicker() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  // This fixed event opens the renderer's ordinary browser file input.
  // No path or filesystem API is exposed to the renderer.
  mainWindow.webContents.executeJavaScript("window.dispatchEvent(new Event('bim-open-file'))", true).catch(() => {});
}

function createMenu() {
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: label('文件', 'File'), submenu: [
      { label: label('打开模型…', 'Open model…'), accelerator: 'CmdOrCtrl+O', click: openFilePicker },
      { type: 'separator' },
      { role: 'close', label: label('关闭窗口', 'Close window') },
      { role: 'quit', label: label('退出', 'Exit') },
    ] },
    { label: label('查看', 'View'), submenu: [
      { role: 'togglefullscreen', label: label('全屏', 'Full screen') },
      { role: 'resetZoom', label: label('实际大小', 'Actual size') },
      { role: 'zoomIn', label: label('放大界面', 'Zoom in') },
      { role: 'zoomOut', label: label('缩小界面', 'Zoom out') },
    ] },
    { label: label('帮助', 'Help'), submenu: [
      { label: label('Revit 插件…', 'Revit plugin…'), click: () => {
        mainWindow?.webContents.executeJavaScript("window.dispatchEvent(new Event('bim-open-revit'))").catch(() => {});
      } },
      { label: label('更新…', 'Update…'), click: () => {
        mainWindow?.webContents.executeJavaScript("window.dispatchEvent(new Event('bim-open-updates'))").catch(() => {});
      } },
      { type: 'separator' },
      { label: label('关于崛亮视觉 · Bright Vision', 'About Bright Vision'), click: () => {
        dialog.showMessageBox(mainWindow, {
          type: 'info', title: '崛亮视觉 · Bright Vision',
          message: `崛亮视觉 · Bright Vision ${app.getVersion()}`,
          detail: label('版权所有 © 2026 崛亮视觉 / Bright Vision。\n在本机独立打开最大 2 GB 的模型 ZIP。模型数据保留在本机。\n项目许可证与署名见 LICENSE、NOTICE；第三方组件见 THIRD-PARTY-NOTICES.md。', 'Copyright © 2026 崛亮视觉 / Bright Vision.\nOpen model ZIP files up to 2 GB locally. Model data stays on this device.\nSee LICENSE, NOTICE and THIRD-PARTY-NOTICES.md for license and attribution details.'),
          buttons: [label('确定', 'OK')], noLink: true,
        });
      } },
    ] },
  ]));
}

function createWindow() {
  mainWindow = new BrowserWindow({
    icon: path.join(__dirname, 'assets/bv.ico'),
    width: 1480, height: 940, minWidth: 960, minHeight: 640,
    show: false, backgroundColor: '#101923', title: '崛亮视觉 · Bright Vision',
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webviewTag: false,
      webSecurity: true,
      allowRunningInsecureContent: false,
      spellcheck: false,
    },
  });
  mainWindow.once('ready-to-show', () => {
    if (!process.argv.includes('--test-hidden')) mainWindow?.show();
  });
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  mainWindow.webContents.on('will-navigate', (event, url) => {
    if (url !== VIEWER_URL) event.preventDefault();
  });
  mainWindow.webContents.on('will-attach-webview', (event) => event.preventDefault());
  mainWindow.on('closed', () => { archiveRead?.abort(); mainWindow = null; });
  mainWindow.loadURL(VIEWER_URL).catch((error) => {
    dialog.showErrorBox('查看器启动失败', `无法加载本地界面。请重新构建或安装程序。\n${error.message}`);
    app.quit();
  });
}

app.whenReady().then(async () => {
  const trusted = event => event.sender === mainWindow?.webContents && event.senderFrame?.url === VIEWER_URL;
  setupRevitIntegration({ getWindow: () => mainWindow, trusted, getLanguage: () => uiLanguage });
  const updates = await setupDesktopUpdates({ getWindow: () => mainWindow, trusted, onLanguage: language => {
    uiLanguage = language;
    createMenu();
    mainWindow?.setTitle('崛亮视觉 · Bright Vision');
  } });
  const publish = result => {
    archiveModels.clear(); archiveModels.set(result.id, result);
    return { url: `app://viewer/models/${result.id}`, name: result.name, size: result.size, cacheId: result.id };
  };
  ipcMain.handle('bim:open-archive-entry', async (event, request) => {
    if (!trusted(event) || archiveRead || !request || typeof request !== 'object') throw new Error('当前无法读取包内模型。');
    if (request.kind !== 'zip') throw new Error('原始 RVT 尚未支持独立读取，请选择已导出的模型 ZIP。');
    const input = request.cacheId ? archiveModels.get(request.cacheId)?.path : request.input;
    if (!input) throw new Error('模型包已关闭，请重新选择文件。');
    const controller = new AbortController(); archiveRead = controller;
    try {
      event.sender.send('bim:archive-read-progress', { phase: '读取包内模型', progress: 0.03 });
      const result = await extractArchiveEntry({ input, index: request.index, kind: request.kind, name: request.name,
        root: path.join(app.getPath('userData'), 'archive-cache'),
        helperDirectory: app.isPackaged ? path.join(process.resourcesPath, 'native-helper') : path.join(app.getAppPath(), 'native', 'generated'),
        signal: controller.signal });
      return publish(result);
    } finally { if (archiveRead === controller) archiveRead = null; }
  });
  ipcMain.on('bim:cancel-archive-read', event => { if (event.sender === mainWindow?.webContents) archiveRead?.abort(); });
  const root = path.join(app.getAppPath(), 'dist');
  protocol.handle('app', async (request) => {
    const url = new URL(request.url);
    if (url.hostname !== 'viewer' || url.username || url.password || url.port) return new Response('Not found', { status: 404 });
    if (request.method !== 'GET' && request.method !== 'HEAD') return new Response('Method not allowed', { status: 405 });
    if (url.pathname.startsWith('/models/')) {
      const model = archiveModels.get(url.pathname.slice('/models/'.length));
      if (!model) return new Response('Not found', { status: 404 });
      const range = /^bytes=(\d+)-(\d+)$/.exec(request.headers.get('Range') || '');
      if (!range) return new Response('Range required', { status: 416 });
      const start = Number(range[1]); const end = Number(range[2]);
      if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end < start || end >= model.size || end - start + 1 > 128 * 1024 * 1024) return new Response('Invalid range', { status: 416 });
      return new Response(Readable.toWeb(createReadStream(model.path, { start, end })), { status: 206,
        headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(end - start + 1), 'Content-Range': `bytes ${start}-${end}/${model.size}`, 'Accept-Ranges': 'bytes' } });
    }
    const filePath = resolveAssetPath(root, url.pathname);
    if (!filePath) return new Response('Not found', { status: 404 });
    try {
      const data = await readFile(filePath);
      return new Response(request.method === 'HEAD' ? null : data, { headers: responseHeaders(filePath) });
    } catch {
      return new Response('Not found', { status: 404 });
    }
  });
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  // The packaged viewer uses bundled scripts and local Blob textures only.
  // Updates use filesystem access in the main process; web traffic stays blocked.
  session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*'] }, (_details, callback) => callback({ cancel: true }));
  createMenu();
  createWindow();
  void updates.check();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
}).catch((error) => {
  dialog.showErrorBox('查看器启动失败', error.message);
  app.quit();
});

app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
