import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium, _electron } from 'playwright';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist');
const expectedVersion = JSON.parse(await readFile('package.json', 'utf8')).version;
const officialConfiguration = JSON.parse(await readFile('electron/update-release.json', 'utf8'));
const errors = [];
const outsideRequests = [];
const cjk = /[\u3400-\u9fff]/u;
await mkdir('test-results', { recursive: true });
const server = createServer(async (request, response) => {
  const file = resolveAssetPath(root, new URL(request.url, 'http://localhost').pathname);
  if (!file) { response.writeHead(404).end(); return; }
  try { const bytes = await readFile(file); response.writeHead(200, responseHeaders(file)); response.end(bytes); }
  catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
let browser;
let desktop;

function captureErrors(page, label) {
  page.on('pageerror', error => errors.push(`${label}: ${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error' && !/favicon.*404/.test(message.text())) errors.push(`${label}: ${message.text()}`);
  });
}

const settle = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
const count = page => page.locator('#component-count').textContent();
const selectedIds = page => page.locator('.component-row.selected').evaluateAll(rows => rows.map(row => row.dataset.id).sort());

async function waitForDesktopInfo(page, predicate, timeoutMs = 10000) {
  // waitForFunction polls synchronous predicates. Poll asynchronous IPC from
  // Node instead so an unresolved Promise cannot satisfy the wait early.
  const deadline = Date.now() + timeoutMs;
  let info;
  do {
    info = await page.evaluate(() => window.desktopApp.getInfo());
    if (predicate(info)) return info;
    await new Promise(resolve => setTimeout(resolve, 25));
  } while (Date.now() < deadline);
  assert.fail(`Desktop state did not settle: ${JSON.stringify(info)}`);
}

async function language(page, value) {
  const current = await page.locator('html').getAttribute('lang');
  const expected = value === 'en' ? 'en' : 'zh-CN';
  if (current !== expected) await page.locator('#language-toggle').click();
  await page.waitForFunction(expected => document.documentElement.lang === expected, expected);
  await settle(page);
}

async function noChinese(locator, label) {
  assert.ok(await locator.count() > 0, `${label} must exist`);
  const text = (await locator.allTextContents()).join(' ').replaceAll('崛亮视觉', 'Bright Vision');
  assert.ok(!cjk.test(text), `${label} is still Chinese: ${text}`);
}

async function englishChrome(page) {
  const labels = {
    'open-button': /Open model/i, 'help-button': /Help/i, 'update-button': /Update/i,
    'fit-all': /Fit all/i, 'fit-selection': /Focus/i, 'hide-selection': /Hide/i,
    'isolate-selection': /Isolate/i, 'show-all': /Show all/i,
    'section-tool': /Section box/i, 'measurement-tool': /Measure/i,
    'views-tool': /Viewpoints/i, 'audit-tool': /Statistics.*review/i,
  };
  for (const [id, expected] of Object.entries(labels)) assert.match(await page.locator(`#${id}`).textContent(), expected, id);
  for (const selector of ['#brand-name', '#brand-subtitle', '.panel-title h2', '.viewer-toolbar', '.viewer-toolsbar', '#version-label'])
    await noChinese(page.locator(selector), selector);
  for (const selector of ['#search', '#close-model', '#viewport canvas']) {
    for (const attribute of ['aria-label', 'placeholder', 'title']) {
      const value = await page.locator(selector).getAttribute(attribute);
      if (value) assert.ok(!cjk.test(value), `${selector}[${attribute}] is still Chinese: ${value}`);
    }
  }
  assert.equal(await page.locator('#language-toggle').textContent(), '中文');
}

async function demo(page) {
  await page.locator('#demo-button').click();
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '4');
  await page.locator('.component-row[data-id="demo-wall"]').waitFor();
  await settle(page);
}

async function webUpdates(page) {
  assert.equal(await page.evaluate(() => typeof window.desktopApp), 'undefined');
  await page.locator('#update-button').click();
  await page.locator('#update-dialog').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#update-title').textContent(), 'Update');
  assert.match(await page.locator('#update-status').textContent(), /Updates are available in the Windows desktop app/i);
  assert.equal(await page.locator('#update-check').isDisabled(), true);
  assert.equal(await page.locator('#update-source').isDisabled(), true);
  assert.equal(await page.locator('#update-install').isVisible(), false);
  await noChinese(page.locator('#update-dialog'), 'web update dialog');
  await page.screenshot({ path: 'test-results/update-web-en.png' });
  await page.locator('#update-close').click();
}

async function englishTools(page) {
  const wall = page.locator('.component-row[data-id="demo-wall"] .component-name');
  const column = page.locator('.component-row[data-id="demo-column"] .component-name');
  await wall.click(); await column.click({ modifiers: ['Control'] });
  assert.deepEqual(await selectedIds(page), ['demo-column', 'demo-wall']);
  assert.match(await page.locator('#selection-summary').textContent(), /2.*component/i);
  await noChinese(page.locator('.multi-selection'), 'multi-selection summary');
  await page.evaluate(() => { globalThis.__languageRegressionCanvas = document.querySelector('canvas'); });
  const batches = await page.locator('#model-stats').getAttribute('data-batches');
  await language(page, 'zh');
  assert.deepEqual(await selectedIds(page), ['demo-column', 'demo-wall']);
  assert.equal(await count(page), '4');
  await language(page, 'en');
  assert.deepEqual(await selectedIds(page), ['demo-column', 'demo-wall']);
  assert.equal(await page.evaluate(() => globalThis.__languageRegressionCanvas === document.querySelector('canvas')), true, 'language switch must not recreate the renderer');
  assert.equal(await page.locator('#model-stats').getAttribute('data-batches'), batches);
  await page.locator('#hide-selection').click();
  await noChinese(page.locator('#footer-status'), 'hidden-state footer');
  assert.match(await page.locator('#footer-status').textContent(), /2.*hidden|hidden.*2/i);
  await page.locator('#show-all').click();
  await wall.click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  await noChinese(page.locator('.property-tabs'), 'property source tabs');
  await page.locator('#ghost-mode').check(); await page.locator('#isolate-selection').click();
  await noChinese(page.locator('#footer-status'), 'ghost-state footer');
  assert.match(await page.locator('#footer-status').textContent(), /ghost|context|background/i);

  await page.locator('#section-tool').click();
  assert.equal(await page.locator('#tools-title').textContent(), 'Section box');
  await noChinese(page.locator('#section-controls'), 'section tool');
  await page.locator('#section-selection').click();
  assert.match(await page.locator('#section-status').textContent(), /section|clipp/i);
  const bounds = await page.locator('.section-coordinates input').evaluateAll(inputs => inputs.map(input => input.value));
  assert.ok(bounds.every(value => value !== '' && Number.isFinite(Number(value))));
  await language(page, 'zh');
  assert.equal(await page.locator('#ghost-mode').isChecked(), true);
  assert.deepEqual(await page.locator('.section-coordinates input').evaluateAll(inputs => inputs.map(input => input.value)), bounds);
  await language(page, 'en');
  assert.equal(await page.locator('#tools-title').textContent(), 'Section box');
  assert.deepEqual(await page.locator('.section-coordinates input').evaluateAll(inputs => inputs.map(input => input.value)), bounds);
  await noChinese(page.locator('#section-status'), 'translated section state');

  await page.locator('#views-tool').click();
  assert.match(await page.locator('#tools-title').textContent(), /viewpoint/i);
  await noChinese(page.locator('#views-controls'), 'viewpoints tool');
  await page.locator('#viewpoint-name').fill('English regression viewpoint');
  await page.locator('#viewpoint-save').click();
  assert.equal(await page.locator('#viewpoint-select option').last().textContent(), 'English regression viewpoint');
  await noChinese(page.locator('#viewpoint-note'), 'saved viewpoint count');
  await page.locator('#tools-close').click(); await page.locator('#show-all').click();
  await page.locator('#clear-selection').click();
  await page.locator('#views-tool').click(); await page.locator('#viewpoint-restore').click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  assert.equal(await page.locator('#ghost-mode').isChecked(), true);
  assert.match(await page.locator('#toast').textContent(), /Viewpoint restored: English regression viewpoint/i);
  await page.locator('#section-tool').click(); await page.locator('#section-clear').click();
  await page.locator('#tools-close').click(); await page.locator('#show-all').click(); await page.locator('#fit-all').click();

  await page.locator('#measurement-tool').click();
  await noChinese(page.locator('#measurement-controls'), 'measurement tool');
  assert.equal(await page.locator('#source-unit').inputValue(), 'unknown');
  await page.locator('#measurement-start').click();
  await noChinese(page.locator('#measurement-result'), 'measurement start state');
  const canvas = await page.locator('canvas').boundingBox();
  let measured = false;
  for (const [x, y] of [[.32, .42], [.42, .48], [.29, .56], [.38, .61], [.2, .5], [.45, .55], [.35, .52]]) {
    await page.mouse.click(canvas.x + canvas.width * x, canvas.y + canvas.height * y);
    const text = await page.locator('#measurement-result').textContent();
    await noChinese(page.locator('#measurement-result'), 'measurement point state');
    if (/\d[\d,.]*\s+model units/i.test(text)) { measured = true; break; }
  }
  assert.ok(measured, 'two actual surface clicks must yield an English model-units measurement');
  const measurement = await page.locator('#measurement-result').textContent();
  await language(page, 'zh'); await language(page, 'en');
  assert.equal(await page.locator('#measurement-result').textContent(), measurement, 'language switch must preserve measured points and units');
  await page.locator('#measurement-stop').click(); await page.locator('#measurement-clear').click();
  await noChinese(page.locator('#measurement-result'), 'measurement cleared state');

  await page.locator('#audit-tool').click();
  await noChinese(page.locator('#audit-controls h3'), 'review headings');
  await noChinese(page.locator('#audit-summary'), 'review statistics');
  await noChinese(page.locator('#audit-controls .tool-note'), 'review scope note');
  await page.locator('.category-stat').first().click();
  await page.waitForFunction(() => /components?/i.test(document.querySelector('#list-summary')?.textContent || ''));
  await noChinese(page.locator('#list-summary'), 'filtered component count');
  await page.locator('#category-filter').selectOption('');
  await page.waitForFunction(() => document.querySelectorAll('.component-row').length === 4);
  await page.locator('#tools-close').click();

  await page.locator('#help-button').click();
  await noChinese(page.locator('#help-dialog'), 'help dialog');
  await page.locator('#close-help').click();
  await page.screenshot({ path: 'test-results/language-en-model-tools.png' });
}

async function desktopUpdates() {
  if (process.env.BIM_TEST_ELECTRON !== '1' && !process.env.BIM_TEST_PACKAGED) return { tested: false };
  const profile = await mkdtemp(path.resolve('test-results/update-language-profile-'));
  const executablePath = process.env.BIM_TEST_PACKAGED || require('electron');
  const env = { ...process.env, BIM_TEST_USER_DATA: profile }; delete env.ELECTRON_RUN_AS_NODE;
  const launch = async () => {
    desktop = await _electron.launch({ executablePath, args: [...(process.env.BIM_TEST_PACKAGED ? [] : ['.']), '--test-hidden'], cwd: process.cwd(), env });
    const page = await desktop.firstWindow(); captureErrors(page, 'desktop');
    await page.locator('#welcome').waitFor({ state: 'visible' });
    assert.equal(await desktop.evaluate(({ app }) => app.getPath('userData')), profile, 'desktop regression must use an isolated profile');
    return page;
  };
  let page = await launch();
  assert.equal(await page.locator('html').getAttribute('lang'), 'zh-CN');
  assert.deepEqual(await desktop.evaluate(({ Menu }) => Menu.getApplicationMenu().items.map(item => item.label)), ['文件', '查看', '帮助']);
  await language(page, 'en');
  await waitForDesktopInfo(page, info => info.language === 'en');
  assert.deepEqual(await desktop.evaluate(({ Menu }) => Menu.getApplicationMenu().items.map(item => item.label)), ['File', 'View', 'Help']);
  let officialObservation = null;
  if (officialConfiguration.enabled) {
    await waitForDesktopInfo(page, value => !value.update.busy, 60000);
    const observed = await page.evaluate(() => window.desktopApp.checkUpdates());
    assert.equal(observed.sourceKind, 'github'); assert.equal(observed.officialConfigured, true);
    assert.equal(observed.source, `https://github.com/${officialConfiguration.owner}/${officialConfiguration.repository}/releases`);
    if (process.env.BIM_TEST_EXPECT_EMPTY_GITHUB === '1') {
      assert.ok(['no-compatible-release', 'release-not-found'].includes(observed.code), JSON.stringify(observed));
      assert.equal(observed.latestVersion, null); assert.notEqual(observed.status, 'up-to-date');
    }
    officialObservation = { status: observed.status, code: observed.code, source: observed.source, latestVersion: observed.latestVersion, updateReady: observed.status === 'update-available' };
  }
  const info = await page.evaluate(() => window.desktopApp.getInfo());
  assert.equal(info.version, expectedVersion);
  await page.locator('#update-button').click(); await page.locator('#update-dialog').waitFor({ state: 'visible' });
  assert.match(await page.locator('#update-current').textContent(), new RegExp(expectedVersion.replaceAll('.', '\\.')));
  assert.match(await page.locator('#update-status').textContent(), officialConfiguration.enabled ? /no compatible public release yet|not public yet|Could not verify the official GitHub update|latest visible version/i : /official GitHub repository has not been configured.*No online request/i);
  assert.equal(await page.locator('#update-check').isEnabled(), true);
  assert.equal(await page.locator('#update-source').isEnabled(), true);
  assert.equal(await page.locator('#update-install').isVisible(), false);
  await page.locator('#update-check').click();
  await waitForDesktopInfo(page, value => !value.update.busy, 60000);
  assert.match(await page.locator('#update-status').textContent(), officialConfiguration.enabled ? /no compatible public release yet|not public yet|Could not verify the official GitHub update|latest visible version/i : /official GitHub repository has not been configured.*No online request/i);
  await noChinese(page.locator('#update-dialog'), 'desktop update dialog');
  await page.screenshot({ path: 'test-results/update-desktop-en.png' });

  // The updater receives an intentionally non-executable test fixture. Its
  // confirmation is always cancelled, so this exercises IPC and verified staging
  // without installing anything or closing the viewer.
  const feed = await mkdtemp(path.resolve('test-results/update-language-feed-'));
  const updateVersion = '0.6.1';
  const fileName = `BIM-Model-Viewer-${updateVersion}-Setup.exe`;
  const payload = Buffer.alloc(64);
  payload.write('MZ');
  payload.write('Bright.Vision update regression fixture; never execute', 2);
  const sha256 = createHash('sha256').update(payload).digest('hex');
  await writeFile(path.join(feed, fileName), payload);
  await writeFile(path.join(feed, 'update.json'), JSON.stringify({
    schemaVersion: 1, productId: 'local.bim.modelviewer', channel: 'stable', version: updateVersion,
    installer: { fileName, sha256, size: payload.length },
  }));
  await desktop.evaluate(({ dialog }, feedDirectory) => {
    globalThis.__updateRegressionDialogs = { sources: 0, confirmations: 0 };
    dialog.showOpenDialog = async () => {
      globalThis.__updateRegressionDialogs.sources++;
      return { canceled: false, filePaths: [feedDirectory] };
    };
    dialog.showMessageBox = async (_window, options) => {
      globalThis.__updateRegressionDialogs.confirmations++;
      globalThis.__updateRegressionDialogs.confirmation = options;
      return { response: 1, checkboxChecked: false };
    };
  }, feed);
  await page.locator('#update-source').click();
  await waitForDesktopInfo(page, info => !info.update.busy && info.update.status === 'update-available');
  await page.waitForFunction(() => document.querySelector('#update-status')?.textContent === 'Version 0.6.1 is available.');
  assert.equal((await page.evaluate(() => window.desktopApp.getInfo())).update.source, feed);
  assert.match(await page.locator('#update-status').textContent(), /Version 0\.6\.1 is available/i);
  assert.equal(await page.locator('#update-install').isVisible(), true);
  await page.locator('#update-check').click();
  await waitForDesktopInfo(page, info => !info.update.busy && info.update.status === 'update-available');
  await page.waitForFunction(() => document.querySelector('#update-status')?.textContent === 'Version 0.6.1 is available.');
  const installation = await page.evaluate(async () => {
    const updates = [];
    const unsubscribe = window.desktopApp.onUpdate(state => updates.push(state));
    try { return { state: await window.desktopApp.installUpdate(), updates }; }
    finally { unsubscribe(); }
  });
  assert.equal(installation.state.status, 'update-available', 'cancelling must return to the verified available update');
  assert.equal(installation.state.busy, false);
  assert.ok(installation.updates.some(state => state.status === 'staged'), 'IPC must publish the verified staged state before confirmation');
  assert.ok(installation.updates.every(state => !('stagedPath' in state)), 'renderer update events must not expose the executable cache path');
  const dialogs = await desktop.evaluate(() => globalThis.__updateRegressionDialogs);
  assert.equal(dialogs.sources, 1);
  assert.equal(dialogs.confirmations, 1, 'verified update must request installation confirmation');
  assert.equal(dialogs.confirmation.defaultId, 1);
  assert.equal(dialogs.confirmation.cancelId, 1);
  assert.deepEqual(dialogs.confirmation.buttons, ['Install and close viewer', 'Cancel']);
  assert.match(dialogs.confirmation.message, /0\.6\.1/);
  const cacheRoot = path.join(profile, 'update-cache');
  const stagedDirectories = await readdir(cacheRoot, { withFileTypes: true });
  assert.equal(stagedDirectories.length, 1);
  assert.equal(stagedDirectories[0].isDirectory(), true);
  assert.ok(stagedDirectories[0].name.startsWith(`${updateVersion}-`));
  const cachedInstaller = path.join(cacheRoot, stagedDirectories[0].name, fileName);
  assert.deepEqual(await readFile(cachedInstaller), payload, 'verified installer must be staged byte for byte');
  assert.deepEqual(await readdir(path.dirname(cachedInstaller)), [fileName], 'staging must not leave a temporary installer');
  const secondCancellation = await page.evaluate(() => window.desktopApp.installUpdate());
  assert.equal(secondCancellation.status, 'update-available');
  assert.equal(secondCancellation.busy, false);
  assert.equal(await desktop.evaluate(() => globalThis.__updateRegressionDialogs.confirmations), 2);
  assert.deepEqual(await readdir(cacheRoot), [stagedDirectories[0].name], 'a second cancelled confirmation must reuse the verified cache');
  assert.deepEqual(await readFile(cachedInstaller), payload);
  await page.waitForFunction(() => document.querySelector('#update-status')?.textContent === 'Version 0.6.1 is available.' && !document.querySelector('#update-install').disabled);
  assert.equal(await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().filter(window => !window.isDestroyed()).length), 1, 'cancelling installation must leave the viewer running');
  assert.equal(await page.locator('#update-dialog').isVisible(), true);
  await noChinese(page.locator('#update-dialog'), 'available desktop update dialog');
  await page.screenshot({ path: 'test-results/update-desktop-available-en.png' });
  await page.locator('#update-close').click();
  const preferences = await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences());
  assert.equal(preferences.sandbox, true); assert.equal(preferences.contextIsolation, true); assert.equal(preferences.nodeIntegration, false);
  if (process.env.BIM_TEST_PACKAGED) assert.equal(await desktop.evaluate(({ app }) => app.isPackaged), true);
  await desktop.close(); desktop = undefined;
  page = await launch();
  await page.waitForFunction(() => document.documentElement.lang === 'en');
  assert.deepEqual(await desktop.evaluate(({ Menu }) => Menu.getApplicationMenu().items.map(item => item.label)), ['File', 'View', 'Help']);
  await waitForDesktopInfo(page, info => info.update.source !== '');
  assert.equal((await page.evaluate(() => window.desktopApp.getInfo())).update.source, feed, 'chosen update source must persist in the isolated profile');
  await language(page, 'zh');
  await waitForDesktopInfo(page, info => info.language === 'zh');
  assert.deepEqual(await desktop.evaluate(({ Menu }) => Menu.getApplicationMenu().items.map(item => item.label)), ['文件', '查看', '帮助']);
  await language(page, 'en');
  await waitForDesktopInfo(page, info => info.language === 'en');
  await desktop.evaluate(({ Menu }) => {
    const help = Menu.getApplicationMenu().items.find(item => item.label === 'Help');
    const update = help.submenu.items.find(item => /Update/i.test(item.label));
    if (!update) throw new Error('Native Help menu has no Update action');
    update.click();
  });
  await page.locator('#update-dialog').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#update-title').textContent(), 'Update');
  await desktop.close(); desktop = undefined;
  return { tested: true, packaged: !!process.env.BIM_TEST_PACKAGED, version: info.version, persistedLanguage: true, persistedUpdateSource: true, isolatedProfile: profile, updateVersion, stagedBytes: payload.length, installationCancelled: true, installerExecuted: false, officialObservation };
}

try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true, args: ['--enable-unsafe-swiftshader'] });
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1, locale: 'en-US' });
  await context.route('**/*', route => {
    const request = route.request();
    if (!request.url().startsWith(url) && !request.url().startsWith('blob:')) { outsideRequests.push(request.url()); return route.abort(); }
    assert.equal(request.method(), 'GET', 'language/update UI must never upload model data');
    return route.continue();
  });
  const page = await context.newPage(); captureErrors(page, 'web');
  await page.goto(url); await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('html').getAttribute('lang'), 'zh-CN', 'a fresh profile defaults to Chinese even under an English browser locale');
  assert.equal(await page.locator('#language-toggle').textContent(), 'English');
  assert.match(await page.locator('#open-button').textContent(), /打开模型/);
  await language(page, 'en'); await englishChrome(page);
  await noChinese(page.locator('#welcome'), 'English welcome');
  await webUpdates(page);
  await demo(page); await englishChrome(page);
  assert.equal(await page.locator('#footer-status').textContent(), 'Model ready');
  assert.match(await page.locator('#list-summary').textContent(), /4 components/i);
  await englishTools(page);
  assert.equal(await page.evaluate(() => localStorage.getItem('bright-vision.language')), 'en');
  await page.reload(); await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('html').getAttribute('lang'), 'en');
  await englishChrome(page); await webUpdates(page);
  await demo(page);
  await page.locator('.component-row[data-id="demo-wall"] .component-name').click();
  await language(page, 'zh');
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  assert.equal(await count(page), '4');
  assert.equal(await page.evaluate(() => localStorage.getItem('bright-vision.language')), 'zh');
  await page.reload(); await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('html').getAttribute('lang'), 'zh-CN');
  await context.close();
  const desktopResult = await desktopUpdates();
  assert.deepEqual(errors, []);
  assert.deepEqual(outsideRequests, []);
  console.log(JSON.stringify({ result: 'PASS', version: expectedVersion, defaultChinese: true, englishTools: true, selectionPreserved: true, measurementPreserved: true, languagePersistence: true, webUpdateFallback: true, desktop: desktopResult, consoleErrors: errors.length, outsideRequests: outsideRequests.length }));
} finally {
  await desktop?.close(); await browser?.close(); await new Promise(resolve => server.close(resolve));
}
