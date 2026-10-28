import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist');
const version = JSON.parse(await readFile('package.json', 'utf8')).version;
const errors = [], outsideRequests = [];
await mkdir('test-results', { recursive: true });
const server = createServer(async (request, response) => {
  const file = resolveAssetPath(root, new URL(request.url, 'http://localhost').pathname);
  if (!file) { response.writeHead(404).end(); return; }
  try { response.writeHead(200, responseHeaders(file)); response.end(await readFile(file)); }
  catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
let browser;
const noChinese = async locator => assert.doesNotMatch((await locator.allTextContents()).join(' '), /[\u3400-\u9fff]/u);
const settle = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

async function context(mockDesktop = false) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
  await context.route('**/*', route => {
    if (!route.request().url().startsWith(url) && !route.request().url().startsWith('blob:')) {
      outsideRequests.push(route.request().url()); return route.abort();
    }
    return route.continue();
  });
  if (mockDesktop) await context.addInitScript(({ version }) => {
    const copy = value => JSON.parse(JSON.stringify(value));
    let current = { status: 'not-needed', exporterVersion: '1.2.0', hosts: [] };
    let subscriber;
    const calls = { get: 0, install: 0, uninstall: 0 };
    window.__revitTest = {
      calls, rejectGet: false,
      emit(value) { current = copy(value); subscriber?.(copy(current)); },
      finishInstall() { current = { status: 'installed', exporterVersion: '1.2.0', hosts: [{ year: '2020', runtime: 'net45', managed: true, status: 'installed', managedVersion: '1.2.0' }] }; subscriber?.(copy(current)); window.__resolveRevitInstall(copy(current)); },
    };
    const update = { status: 'not-configured', currentVersion: version };
    window.desktopApp = {
      getInfo: async () => ({ version, language: 'zh', update }), setLanguage: async value => value,
      checkUpdates: async () => update, chooseUpdateSource: async () => update, installUpdate: async () => update, onUpdate: () => () => {},
      getRevitStatus: async () => { calls.get++; if (window.__revitTest.rejectGet) throw new Error('Injected service failure'); return copy(current); },
      installRevitPlugin: () => { calls.install++; current = { ...current, status: 'busy' }; subscriber?.(copy(current)); return new Promise(resolve => { window.__resolveRevitInstall = resolve; }); },
      uninstallRevitPlugin: async () => { calls.uninstall++; current = { status: 'ready', exporterVersion: '1.2.0', hosts: [{ year: '2020', runtime: 'net45', managed: false, status: 'ready' }], code: 'uninstalled' }; subscriber?.(copy(current)); return copy(current); },
      onRevitStatus: callback => { subscriber = callback; return () => { subscriber = undefined; }; },
    };
  }, { version });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !/favicon.*404/.test(message.text())) errors.push(message.text()); });
  await page.goto(url);
  await page.locator('#welcome').waitFor({ state: 'visible' });
  return { page, context };
}

try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true, args: ['--enable-unsafe-swiftshader'] });
  const web = await context();
  assert.equal(await web.page.locator('#brand-name').textContent(), 'Bright Vision');
  assert.equal(await web.page.locator('#brand-subtitle').textContent(), '崛亮视觉 · 本地BIM工作台');
  await web.page.locator('#revit-button').click();
  await web.page.locator('#revit-dialog').waitFor({ state: 'visible' });
  assert.match(await web.page.locator('#revit-version').textContent(), /1\.7\.0/);
  assert.match(await web.page.locator('#revit-status').textContent(), /网页版不提供本地插件安装/);
  assert.match(await web.page.locator('#revit-optional').textContent(), /没有 Revit/);
  for (const id of ['revit-refresh', 'revit-install', 'revit-uninstall']) assert.equal(await web.page.locator(`#${id}`).isDisabled(), true);
  await web.page.locator('#revit-close').click();
  await web.page.locator('#language-toggle').click();
  assert.equal(await web.page.locator('#brand-subtitle').textContent(), 'Local BIM workspace');
  await web.page.locator('#help-button').click();
  await web.page.locator('#help-revit-button').click();
  assert.equal(await web.page.locator('#help-dialog').isVisible(), false);
  await noChinese(web.page.locator('#revit-dialog'));
  assert.match(await web.page.locator('#revit-status').textContent(), /web version cannot install/i);
  await web.page.screenshot({ path: 'test-results/revit-ui-web-en.png' });
  await web.page.locator('#revit-close').click();
  await web.page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await web.page.locator('#revit-button').isVisible(), true);
  assert.equal(await web.page.locator('#language-toggle').isVisible(), true);
  assert.ok(await web.page.locator('#open-button').evaluate(element => element.getBoundingClientRect().right <= innerWidth));
  await web.context.close();

  const desktop = await context(true);
  const page = desktop.page;
  await page.locator('#language-toggle').click();
  await page.locator('#demo-button').click();
  await page.waitForFunction(() => document.getElementById('component-count').textContent === '4');
  await page.locator('.component-row[data-id="demo-wall"] .component-name').click();
  await page.evaluate(() => { window.__revitCanvas = document.querySelector('canvas'); });
  await page.locator('#revit-button').click();
  await page.waitForFunction(() => document.getElementById('revit-status').dataset.status === 'not-needed');
  assert.match(await page.locator('#revit-status').textContent(), /No Revit installation/i);
  assert.equal(await page.locator('#revit-install').isDisabled(), true);
  await noChinese(page.locator('#revit-dialog'));

  const ready = { status: 'ready', exporterVersion: '1.2.0', hosts: [{ year: '2020', runtime: 'net45', managed: false, status: 'ready' }] };
  await page.evaluate(state => window.__revitTest.emit(state), ready);
  assert.equal(await page.locator('#revit-hosts tr[data-year="2020"]').textContent(), 'Revit 2020.NET Framework 4.5Not registered');
  assert.equal(await page.locator('#revit-install').isEnabled(), true);
  assert.equal(await page.locator('#revit-uninstall').isDisabled(), true);
  await page.keyboard.press('h');
  assert.doesNotMatch(await page.locator('#footer-status').textContent(), /hidden/i);
  await page.locator('#revit-install').click();
  assert.equal(await page.locator('#revit-install').isDisabled(), true);
  assert.equal(await page.locator('#revit-refresh').isDisabled(), true);
  await page.evaluate(() => window.__revitTest.finishInstall());
  await page.waitForFunction(() => document.getElementById('revit-status').dataset.status === 'installed');
  assert.equal(await page.locator('#revit-uninstall').isEnabled(), true);
  assert.match(await page.locator('#revit-status').textContent(), /Restart Revit/i);
  assert.match(await page.locator('#revit-status').textContent(), /Civil raw quantity workbench.*BV → bright vision/);
  assert.equal(await page.evaluate(() => window.__revitTest.calls.install), 1);
  await page.evaluate(() => document.getElementById('language-toggle').click());
  assert.match(await page.locator('#revit-status').textContent(), /导出插件已注册/);
  assert.match(await page.locator('#revit-status').textContent(), /BV → bright vision.*土建原始量工作台.*本地导出模型 ZIP/);
  await page.evaluate(() => document.getElementById('language-toggle').click());
  await noChinese(page.locator('#revit-dialog'));
  assert.equal(await page.evaluate(() => window.__revitCanvas === document.querySelector('canvas')), true);
  assert.equal(await page.locator('.component-row[data-id="demo-wall"]').getAttribute('class'), 'component-row selected');

  await page.evaluate(state => window.__revitTest.emit({ ...state, status: 'deferred', code: 'running-revit' }), ready);
  assert.match(await page.locator('#revit-status').textContent(), /Revit is running/i);
  assert.equal(await page.locator('#revit-install').isDisabled(), true);
  assert.equal(await page.locator('#revit-uninstall').isDisabled(), true);
  assert.equal(await page.locator('#revit-refresh').isEnabled(), true);
  await page.evaluate(state => window.__revitTest.emit(state), ready);
  await page.locator('#revit-refresh').click();
  await page.waitForFunction(() => !document.getElementById('revit-refresh').disabled);
  await page.evaluate(() => window.__revitTest.emit({ status: 'failed', exporterVersion: '1.2.0', hosts: [], code: 'fixture-error', message: '<img src=x onerror=alert(1)> 原始诊断' }));
  assert.match(await page.locator('#revit-status').textContent(), /could not be completed/i);
  await page.locator('#revit-diagnostic summary').click();
  assert.match(await page.locator('#revit-diagnostic-text').textContent(), /<img src=x/);
  assert.equal(await page.locator('#revit-dialog img').count(), 0);
  await page.evaluate(() => window.__revitTest.emit({ status: 'installed', exporterVersion: '1.2.0', hosts: [{ year: '2020', runtime: 'net45', managed: true, status: 'installed', managedVersion: '1.2.0' }] }));
  await page.locator('#revit-uninstall').click();
  await page.waitForFunction(() => document.getElementById('revit-status').dataset.status === 'ready');
  assert.equal(await page.evaluate(() => window.__revitTest.calls.uninstall), 1);
  assert.equal(await page.locator('#revit-uninstall').isDisabled(), true);
  await page.evaluate(() => { window.__revitTest.rejectGet = true; });
  await page.locator('#revit-refresh').click();
  await page.waitForFunction(() => document.getElementById('revit-status').dataset.status === 'failed');
  assert.equal(await page.locator('#revit-refresh').isEnabled(), true);
  await page.evaluate(state => { window.__revitTest.rejectGet = false; window.__revitTest.emit(state); }, { ...ready, hosts: [{ year: '2026', runtime: 'unknown', managed: false, status: 'unsupported' }] });
  assert.equal(await page.locator('#revit-install').isDisabled(), true);
  assert.match(await page.locator('#revit-hosts').textContent(), /not supported/i);
  await page.evaluate(state => window.__revitTest.emit(state), ready);
  await page.screenshot({ path: 'test-results/revit-ui-desktop-bridge-en.png' });
  await page.locator('#revit-close').click();
  await settle(page);
  assert.equal(await page.locator('#component-count').textContent(), '4');
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  await desktop.context.close();
  assert.deepEqual(errors, []);
  assert.deepEqual(outsideRequests, []);
  console.log(JSON.stringify({ result: 'PASS', webBoundary: true, helpEntry: true, mobileEntry: true, syntheticDesktopStates: true, installRepair: true, uninstall: true, retry: true, languages: true, modelAndSelectionPreserved: true, consoleErrors: errors.length, outsideRequests: outsideRequests.length }));
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
