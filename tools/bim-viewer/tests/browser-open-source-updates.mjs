import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist'), output = path.resolve('test-results/open-source-updates-0.6.0');
await mkdir(output, { recursive: true });
const errors = [], outsideRequests = [], checks = [];
const server = createServer(async (request, response) => {
  const file = resolveAssetPath(root, new URL(request.url, 'http://localhost').pathname);
  if (!file) { response.writeHead(404).end(); return; }
  try { response.writeHead(200, responseHeaders(file)); response.end(await readFile(file)); } catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}/`; let browser;
try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true, args: ['--enable-unsafe-swiftshader'] });
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
  await context.route('**/*', route => { if (!route.request().url().startsWith(origin) && !route.request().url().startsWith('blob:')) { outsideRequests.push(route.request().url()); return route.abort(); } return route.continue(); });
  const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin); await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#brand-name').textContent(), 'Bright Vision'); assert.match(await page.locator('#company-attribution').textContent(), /崛亮视觉/); assert.match(await page.title(), /崛亮视觉/); checks.push('official company attribution visible in header/footer/title');
  await page.locator('#help-button').click(); assert.match(await page.locator('#company-information').textContent(), /崛亮视觉/); assert.match(await page.locator('#company-information').textContent(), /Apache/i); await page.locator('#close-help').click();
  await page.locator('#update-button').click(); assert.equal(await page.locator('#update-official').isDisabled(), true); assert.equal(await page.locator('#update-source').isDisabled(), true); assert.equal(await page.locator('#update-install').isVisible(), false); checks.push('Web update UI offers no native install');
  await page.locator('#update-close').click(); await page.screenshot({ path: path.join(output, 'web-brand-zh.png') }); await context.close();

  const desktop = await browser.newContext({ viewport: { width: 1440, height: 960 } });
  await desktop.route('**/*', route => { if (!route.request().url().startsWith(origin) && !route.request().url().startsWith('blob:')) { outsideRequests.push(route.request().url()); return route.abort(); } return route.continue(); });
  await desktop.addInitScript(() => {
    let subscriber; let update = { status: 'not-configured', currentVersion: '0.6.0', sourceKind: 'github', officialConfigured: false, source: '' };
    window.__updateTest = { calls: { local: 0, official: 0, check: 0, install: 0 }, emit(value) { update = { ...value }; subscriber?.({ ...update }); } };
    window.desktopApp = {
      getInfo: async () => ({ version: '0.6.0', language: 'zh', update: { ...update } }), setLanguage: async language => language,
      checkUpdates: async (...args) => { if (args.length) throw new Error('No URL should be passed.'); window.__updateTest.calls.check++; return { ...update }; },
      useOfficialUpdates: async (...args) => { if (args.length) throw new Error('No URL should be passed.'); window.__updateTest.calls.official++; update = { status: 'not-configured', currentVersion: '0.6.0', sourceKind: 'github', officialConfigured: false, source: '' }; return { ...update }; },
      chooseUpdateSource: async () => { window.__updateTest.calls.local++; update = { status: 'update-available', currentVersion: '0.6.0', latestVersion: '0.6.1', sourceKind: 'local', officialConfigured: false, source: 'C:\\Shared\\updates' }; return { ...update }; },
      installUpdate: async () => { window.__updateTest.calls.install++; return { ...update }; }, onUpdate: callback => { subscriber = callback; return () => {}; },
      getRevitStatus: async () => ({ status: 'not-needed', exporterVersion: '1.7.0', hosts: [] }), onRevitStatus: () => () => {}, installRevitPlugin: async () => ({}), uninstallRevitPlugin: async () => ({}),
    };
  });
  const app = await desktop.newPage(); app.on('pageerror', error => errors.push(error.message)); await app.goto(origin); await app.locator('#welcome').waitFor({ state: 'visible' });
  await app.locator('#update-button').click(); assert.match(await app.locator('#update-status').textContent(), /尚未配置.*不.*请求/); assert.match(await app.locator('#update-source-path').textContent(), /尚未配置/); assert.equal(await app.locator('#update-install').isVisible(), false); checks.push('unconfigured GitHub is explicit without pretending a release exists');
  await app.locator('#update-close').click(); await app.locator('#language-toggle').click(); await app.waitForFunction(() => document.documentElement.lang === 'en'); await app.locator('#update-button').click();
  assert.match(await app.locator('#update-status').textContent(), /not been configured.*No online request/); assert.doesNotMatch(await app.locator('#update-dialog').textContent(), /[\u3400-\u9fff]/u);
  await app.screenshot({ path: path.join(output, 'official-unconfigured-en.png') });
  await app.locator('#update-official').click(); assert.equal(await app.evaluate(() => window.__updateTest.calls.official), 1); checks.push('official source IPC takes no renderer URL');
  await app.locator('#update-source').click(); await app.waitForFunction(() => document.querySelector('#update-source-path').textContent.includes('Shared'));
  assert.equal(await app.locator('#update-install').isVisible(), true); await app.locator('#update-install').click(); assert.equal(await app.evaluate(() => window.__updateTest.calls.install), 1); checks.push('local source remains usable and install action stays behind desktop IPC');
  await app.evaluate(() => window.__updateTest.emit({ status: 'update-available', currentVersion: '0.6.0', latestVersion: '0.6.1', sourceKind: 'github', officialConfigured: true, source: 'https://github.com/BrightVision/bright-vision/releases' }));
  assert.match(await app.locator('#update-source-label').textContent(), /Official GitHub/); assert.match(await app.locator('#update-note').textContent(), /Only public release metadata/); checks.push('configured official channel has a separate explanatory state (mock only)');
  await app.evaluate(() => window.__updateTest.emit({ status: 'error', code: 'no-compatible-release', currentVersion: '0.6.0', sourceKind: 'github', officialConfigured: true, source: 'https://github.com/queqideliang/bright-vision/releases' }));
  assert.match(await app.locator('#update-status').textContent(), /no compatible public release yet/);
  assert.equal(await app.locator('#update-install').isVisible(), false); checks.push('empty official channel never claims an update is ready');
  await app.evaluate(() => window.__updateTest.emit({ status: 'error', currentVersion: '0.6.0', sourceKind: 'local', source: '<img src=x onerror=alert(1)>' }));
  assert.equal(await app.locator('#update-dialog img').count(), 0); assert.match(await app.locator('#update-source-path').textContent(), /<img/); checks.push('source labels use text, not HTML');
  await app.locator('#update-close').click(); await app.locator('#language-toggle').click();
  await app.setViewportSize({ width: 390, height: 844 }); await app.screenshot({ path: path.join(output, 'brand-narrow-zh.png') });
  assert.ok(await app.locator('#company-attribution').isVisible()); assert.ok(await app.locator('#open-button').evaluate(el => el.getBoundingClientRect().right <= innerWidth)); checks.push('company attribution retained at 390px layout');
  assert.deepEqual(outsideRequests, []); assert.deepEqual(errors, []);
  await writeFile(path.join(output, 'report.json'), JSON.stringify({ result: 'PASS', checks, actualOnlineGitHubUsed: false, nativeInstallerExecuted: false, outsideRequests, errors, scope: 'Built Web assets with real Chromium; desktop bridge is mocked.' }, null, 2) + '\n');
  console.log(JSON.stringify({ result: 'PASS', checks: checks.length, output, actualOnlineGitHubUsed: false, nativeInstallerExecuted: false }));
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
