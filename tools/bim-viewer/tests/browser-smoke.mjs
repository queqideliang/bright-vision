import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium, _electron } from 'playwright';
import { strToU8, zipSync } from 'fflate';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist');
await mkdir('test-results', { recursive: true });
const server = createServer(async (req, res) => {
  const file = resolveAssetPath(root, new URL(req.url, 'http://localhost').pathname);
  if (!file) { res.writeHead(404).end(); return; }
  try { const data = await readFile(file); res.writeHead(200, responseHeaders(file)); res.end(data); }
  catch { res.writeHead(404).end(); }
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
const failures = [];
const outsideRequests = [];
const chromePath = process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
let browser;
let desktop;

function fixture() {
  const matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  const json = {
    'components/components-0-0.json': [{ Id: 'test-wall', name: '测试墙', BuildInCategory: '墙', familyName: '基本墙', familySymbol: '测试类型', matrix, Nodes: [0], Property: 'p', SymbolProperty: 't' }],
    'nodes/nodes-0-0.json': { 0: { geometry: 0, material: 'm', matrix } },
    'geometries/geometries-0-0.json': { 0: { type: 'MESH', positions: [-1, 0, 0, 1, 0, 0, 0, 0, 2], indices: [0, 1, 2], uv: [], normals: [] } },
    'materials/materials-0-0.json': { m: { color: [80, 140, 210], opacity: 1 } },
    'properties/properties-0-0.json': { p: { '标识': { '备注': '<img src=x onerror="window.__unsafeModel=true">', '可空参数': null } }, t: { '类型': { '类型参数': '原始类型数据' } } },
    'LevelInfo.json': { '测试层': { attribute: { elevation: 0 }, elements: { '墙': [{ id: 'test-wall', name: '测试墙' }] } } },
  };
  return zipSync(Object.fromEntries(Object.entries(json).map(([key, value]) => [key, strToU8(JSON.stringify(value))])));
}

function captureErrors(page) {
  page.on('pageerror', (error) => failures.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error' && !/favicon\.ico.*404/.test(message.text())) failures.push(message.text()); });
}

async function settle(page) {
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}

async function waitModel(page, expected, timeout = 120000) {
  await page.locator('#load-overlay').waitFor({ state: 'hidden', timeout });
  await page.waitForFunction((value) => document.querySelector('#component-count')?.textContent === value, expected, { timeout });
  await settle(page);
}

try {
  browser = await chromium.launch({ executablePath: chromePath, headless: true, args: ['--enable-unsafe-swiftshader'] });
  const context = await browser.newContext({ viewport: { width: 1480, height: 940 }, deviceScaleFactor: 1 });
  await context.route('**/*', (route) => {
    const request = route.request();
    if (!request.url().startsWith(url) && !request.url().startsWith('blob:')) { outsideRequests.push(request.url()); return route.abort(); }
    assert.equal(request.method(), 'GET', 'the viewer must not send model uploads');
    return route.continue();
  });
  const page = await context.newPage(); captureErrors(page);
  await page.goto(url); await page.locator('#welcome').waitFor({ state: 'visible' });
  await page.locator('#help-button').click();
  assert.doesNotMatch(await page.locator('body').textContent(), /\u76c8\u5609|boswinner|boswiner|bosrevit|\bbos\b/iu);
  await page.locator('#close-help').click();
  await page.screenshot({ path: 'test-results/welcome.png' });
  await page.locator('#demo-button').click(); await waitModel(page, '4');
  await page.locator('.component-name').filter({ hasText: '示例墙' }).click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  await page.locator('#isolate-selection').click();
  assert.match(await page.locator('#footer-status').textContent(), /3/);
  await settle(page); const isolated = await page.locator('canvas').screenshot();
  await page.locator('#show-all').click(); await settle(page);
  assert.ok(!isolated.equals(await page.locator('canvas').screenshot()), 'isolation must change rendered geometry');
  await page.locator('#hide-selection').click(); assert.match(await page.locator('#footer-status').textContent(), /1/);
  await page.locator('.component-name').filter({ hasText: '示例墙' }).click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  assert.equal(await page.locator('#footer-status').textContent(), '模型已就绪');
  await page.locator('#level-filter').selectOption('二层'); assert.equal(await page.locator('.component-row').count(), 1);
  await page.locator('#level-filter').selectOption('');
  await page.locator('#clear-selection').click();
  const bounds = await page.locator('canvas').boundingBox(); let picked = false;
  for (const [x, y] of [[.5, .5], [.5, .6], [.4, .5], [.6, .5], [.5, .4]]) {
    await page.mouse.click(bounds.x + bounds.width * x, bounds.y + bounds.height * y);
    if (await page.locator('.id-row code').count()) { picked = true; break; }
  }
  assert.ok(picked, 'clicking actual model geometry must select a component');
  await page.screenshot({ path: 'test-results/demo.png' });
  await page.locator('#file-input').setInputFiles({ name: 'invalid.zip', mimeType: 'application/zip', buffer: Buffer.from('invalid') });
  await page.locator('#toast').waitFor({ state: 'visible' });
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  assert.equal(await page.locator('#load-error').isVisible(), true, 'import errors must remain visible');
  await page.locator('#file-input').setInputFiles({ name: 'fixture.zip', mimeType: 'application/zip', buffer: Buffer.from(fixture()) });
  await waitModel(page, '1');
  await page.locator('.component-name').click();
  assert.match(await page.locator('.property-groups').textContent(), /<img src=x/);
  assert.equal(await page.evaluate(() => window.__unsafeModel), undefined);
  assert.equal(await page.locator('.property-groups img').count(), 0);
  await page.getByRole('tab', { name: '类型', exact: true }).click();
  assert.match(await page.locator('.property-groups').textContent(), /原始类型数据/);
  const levels = await page.locator('#level-filter option').allTextContents(); assert.ok(levels.some((l) => l.includes('测试层')));
  await page.locator('#close-model').click(); await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('canvas').count(), 1);
  await page.locator('#demo-button').click(); await waitModel(page, '4');
  const bundleBytes = zipSync({ 'A.zip': fixture(), 'B.zip': fixture() });
  await page.locator('#file-input').setInputFiles({ name: 'bundle.zip', mimeType: 'application/zip', buffer: Buffer.from(bundleBytes) });
  await page.locator('#archive-dialog').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#archive-select option').count(), 2);
  await page.locator('#archive-select').selectOption('1');
  await page.locator('#archive-form button').click();
  await waitModel(page, '1'); assert.equal(await page.locator('#model-label').textContent(), 'B.zip');
  await page.locator('#file-input').setInputFiles({ name: 'bundle.zip', mimeType: 'application/zip', buffer: Buffer.from(bundleBytes) });
  await page.locator('#archive-dialog').waitFor({ state: 'visible' });
  await page.locator('#archive-cancel').click();
  await page.locator('#welcome').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#component-count').textContent(), '0');
  await page.locator('#file-input').setInputFiles({ name: 'raw-models.zip', mimeType: 'application/zip', buffer: Buffer.from(zipSync({ 'original.rvt': strToU8('test') })) });
  await page.locator('#archive-dialog').waitFor({ state: 'visible' });
  assert.equal(await page.locator('#archive-select option:disabled').count(), 1);
  assert.equal(await page.locator('#archive-form button').isDisabled(), true);
  await page.locator('#archive-cancel').click();
  if (process.env.BIM_MODEL_TEST_DIR) {
    for (const [index, [file, count]] of [['SNPRI-N-AB-AS-ALL.zip', '45,297'], ['SNPRI-N-CD-AS-ALL.zip', '20,904'], ['SNPRI-N-EF-AS-ALL.zip', '12,711'], ['SNPRI-N-GH-AS-ALL.zip', '19,296'], ['SNPRI_LC.zip', '12,331']].entries()) {
      const begin = performance.now();
      await page.locator('#file-input').setInputFiles(path.join(process.env.BIM_MODEL_TEST_DIR, file));
      await waitModel(page, count);
      const stats = await page.locator('#model-stats').getAttribute('title');
      console.log(JSON.stringify({ sample: index + 1, seconds: +((performance.now() - begin) / 1000).toFixed(2), components: count, rendering: stats }));
      assert.ok(stats && !stats.startsWith('0 '), 'real model must submit GPU draw calls');
      await page.locator('.component-main').first().click(); assert.ok(await page.locator('.id-row code').textContent());
      await page.screenshot({ path: `test-results/model-${index + 1}.png` });
      await page.locator('#show-all').click();
    }
    // Replace an in-flight real scene build with a bad archive. Its old scene
    // must never become visible behind metadata from the failed replacement.
    await page.locator('#file-input').setInputFiles(path.join(process.env.BIM_MODEL_TEST_DIR, 'SNPRI_LC.zip'));
    await page.waitForFunction(() => document.querySelector('#load-phase')?.textContent === '构建三维视图', undefined, { timeout: 120000 });
    await page.locator('#file-input').setInputFiles({ name: 'replacement-invalid.zip', mimeType: 'application/zip', buffer: Buffer.from('invalid replacement') });
    await page.locator('#welcome').waitFor({ state: 'visible' });
    await settle(page);
    assert.equal(await page.locator('#component-count').textContent(), '0');
    assert.equal(await page.locator('.component-row').count(), 0);
    assert.equal(await page.locator('canvas').count(), 1);
  }
  if (process.env.BIM_CONVERTED_MODEL) {
    await page.locator('#file-input').setInputFiles(process.env.BIM_CONVERTED_MODEL);
    await waitModel(page, '5,752');
    const stats = await page.locator('#model-stats').getAttribute('title');
    assert.ok(stats && !stats.startsWith('0 '), 'converted RVT must produce visible GPU geometry');
    await page.locator('.component-main').first().click();
    assert.ok(await page.locator('.id-row code').textContent());
    await page.screenshot({ path: 'test-results/converted-rvt.png' });
    console.log(JSON.stringify({ convertedRvt: true, components: '5,752', rendering: stats }));
  }
  const failureContext = await browser.newContext();
  const failurePage = await failureContext.newPage();
  const contextErrors = []; failurePage.on('pageerror', (error) => contextErrors.push(error.message));
  await failurePage.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    let contexts = 0;
    HTMLCanvasElement.prototype.getContext = function (type, ...args) {
      if (String(type).startsWith('webgl') && ++contexts > 1) return null;
      return original.call(this, type, ...args);
    };
  });
  await failurePage.goto(url);
  await failurePage.locator('#demo-button').click(); await waitModel(failurePage, '4');
  await failurePage.locator('#file-input').setInputFiles({ name: 'fixture.zip', mimeType: 'application/zip', buffer: Buffer.from(fixture()) });
  await failurePage.locator('#open-button').waitFor({ state: 'visible' });
  assert.equal(await failurePage.locator('#open-button').isDisabled(), true);
  assert.equal(await failurePage.locator('#component-count').textContent(), '0');
  await failurePage.keyboard.press('Escape'); assert.deepEqual(contextErrors, []);
  await failureContext.close();
  if (process.env.BIM_TEST_ELECTRON === '1' || process.env.BIM_TEST_PACKAGED) {
    const executablePath = process.env.BIM_TEST_PACKAGED || require('electron');
    const env = { ...process.env }; delete env.ELECTRON_RUN_AS_NODE;
    desktop = await _electron.launch({ executablePath, args: [...(process.env.BIM_TEST_PACKAGED ? [] : ['.']), '--test-hidden'], cwd: process.cwd(), env });
    const window = await desktop.firstWindow(); captureErrors(window);
    await window.locator('#welcome').waitFor({ state: 'visible' });
    assert.equal(await window.evaluate(() => typeof window.localBim?.convertRvt), 'undefined', 'no authoring application conversion bridge may be exposed');
    assert.equal(await window.evaluate(() => typeof window.localBim?.openArchiveEntry), 'function');
    await window.locator('#file-input').setInputFiles({ name: 'fixture.zip', mimeType: 'application/zip', buffer: Buffer.from(fixture()) });
    await waitModel(window, '1');
    await window.locator('.component-name').click();
    assert.equal(await window.locator('.id-row code').textContent(), 'test-wall');
    await window.locator('#file-input').setInputFiles({ name: 'original.rvt', mimeType: 'application/octet-stream', buffer: Buffer.from('test') });
    await window.locator('#load-error').waitFor({ state: 'visible' });
    assert.match(await window.locator('#load-error-message').textContent(), /RVT.*独立读取/u);
    assert.equal(await window.locator('#component-count').textContent(), '1');
    await window.locator('#dismiss-error').click();
    const blocked = await window.evaluate(async () => {
      try {
        await window.localBim.openArchiveEntry(new File(['test'], 'test.zip'), { index: 0, kind: 'rvt', name: 'original.rvt' });
        return '';
      } catch (error) { return error.message; }
    });
    assert.match(blocked, /RVT.*独立读取/u);
    const options = await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences());
    assert.equal(options.sandbox, true); assert.equal(options.contextIsolation, true); assert.equal(options.nodeIntegration, false);
    if (process.env.BIM_TEST_PACKAGED) assert.equal(await desktop.evaluate(({ app }) => app.isPackaged), true);
    if (process.env.BIM_MODEL_TEST_DIR) {
      await window.locator('#file-input').setInputFiles(path.join(process.env.BIM_MODEL_TEST_DIR, 'AB（不带设备）.zip'));
      await window.locator('#archive-dialog').waitFor({ state: 'visible' });
      assert.equal(await window.locator('#archive-select option').count(), 26);
      await window.locator('#archive-select').selectOption('0');
      await window.locator('#archive-form button').click();
      await waitModel(window, '45,174');
      assert.equal(await window.locator('#load-error').isVisible(), false);
      await window.locator('.component-name').first().click();
      assert.ok(await window.locator('.id-row code').textContent());
      await window.screenshot({ path: 'test-results/packaged-nested-model.png' });
      console.log('Packaged desktop: real 26-model outer ZIP, native extraction, range reads, rendering and selection passed.');
    }
    console.log('Electron local protocol, worker, model loading and sandbox verified.');
  }
  assert.deepEqual(outsideRequests, []);
  assert.deepEqual(failures, []);
  console.log('Browser smoke passed: render, pick, isolate, hide, filter, properties, malformed ZIP recovery, inert model text, reopen and no remote requests.');
} finally {
  await desktop?.close(); await browser?.close(); await new Promise((resolve) => server.close(resolve));
}
