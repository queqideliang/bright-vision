import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { unzipSync, strFromU8 } from 'fflate';
import { exerciseWorkbench, exerciseVirtualTree } from './workbench-scenarios.mjs';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist');
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
const errors = [];
const requests = [];
const settle = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true, args: ['--enable-unsafe-swiftshader'] });
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
  await context.route('**/*', route => {
    if (!route.request().url().startsWith(url) && !route.request().url().startsWith('blob:')) {
      requests.push(route.request().url()); return route.abort();
    }
    assert.equal(route.request().method(), 'GET');
    return route.continue();
  });
  // Observe allocations in this isolated test context. These hooks are never
  // shipped in the product and cannot read model fields or another window.
  await context.addInitScript(() => {
    const contexts = new Map();
    const resources = context => {
      let live = contexts.get(context);
      if (!live) { live = { buffers: new Set(), textures: new Set(), programs: new Set() }; contexts.set(context, live); }
      return live;
    };
    for (const name of ['WebGLRenderingContext', 'WebGL2RenderingContext']) {
      const prototype = globalThis[name]?.prototype;
      if (!prototype) continue;
      for (const [kind, suffix] of [['buffers', 'Buffer'], ['textures', 'Texture'], ['programs', 'Program']]) {
        const create = prototype[`create${suffix}`], remove = prototype[`delete${suffix}`];
        prototype[`create${suffix}`] = function (...args) { const value = create.apply(this, args); if (value) resources(this)[kind].add(value); return value; };
        prototype[`delete${suffix}`] = function (value) { resources(this)[kind].delete(value); return remove.call(this, value); };
      }
    }
    globalThis.__bimGlSnapshot = () => {
      const total = { buffers: 0, textures: 0, programs: 0, contexts: 0 };
      for (const [context, live] of contexts) {
        // forceContextLoss releases resources without issuing deleteTexture
        // for the renderer's four fallback textures. Exclude lost contexts.
        if (context.isContextLost()) continue;
        total.contexts++;
        for (const key of ['buffers', 'textures', 'programs']) total[key] += live[key].size;
      }
      return total;
    };
  });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !/favicon.*404/.test(message.text())) errors.push(message.text()); });
  await page.goto(url);
  await page.locator('#demo-button').click();
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '4');
  console.log(JSON.stringify({ workbench: await exerciseWorkbench(page) }));
  await page.locator('.component-name').filter({ hasText: '示例墙' }).click();
  await page.locator('#ghost-mode').check(); await page.locator('#isolate-selection').click();
  await page.locator('#section-tool').click(); await page.locator('#section-selection').click();
  await settle(page); await page.screenshot({ path: 'test-results/workbench-ghost-section.png' });
  console.log(JSON.stringify({ virtualTree: await exerciseVirtualTree(page), rendering: await page.locator('#model-stats').getAttribute('title') }));
  assert.equal(Number(await page.locator('#model-stats').getAttribute('data-instanced-placements')), 5000, 'repeat geometry must use the instanced rendering route');
  await page.locator('#measurement-tool').click(); await page.locator('#measurement-start').click();
  const canvas = await page.locator('canvas').boundingBox();
  let hitInstance = false;
  for (const [x, y] of [[.35, .4], [.45, .5], [.3, .6], [.2, .5], [.4, .6], [.5, .5]]) {
    await page.mouse.click(canvas.x + canvas.width * x, canvas.y + canvas.height * y);
    if ((await page.locator('#measurement-result').textContent()).includes('第一点')) { hitInstance = true; break; }
  }
  assert.ok(hitInstance, 'actual GPU instances must remain pickable through their local-space triangle index');
  await page.locator('#measurement-stop').click(); await page.locator('#measurement-clear').click(); await page.locator('#tools-close').click();
  await page.screenshot({ path: 'test-results/workbench-instanced-5000.png' });

  const writerFixture = path.resolve('../bright-revit-exporter/artifacts/export-fixture.zip');
  await page.locator('#file-input').setInputFiles(writerFixture);
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '2');
  assert.equal(await page.locator('#source-unit').inputValue(), 'm');
  await settle(page);
  assert.ok(Number((await page.locator('#model-stats').getAttribute('title')).split(' ')[0]) > 0);
  await page.screenshot({ path: 'test-results/workbench-exporter-mirrored-fixture.png' });
  console.log(JSON.stringify({ exporterWriterFixture: true, mirroredAndTranslatedGeometry: true, unit: 'm' }));

  const sample = process.env.BIM_WORKBENCH_MODEL || path.resolve('../../test/models/Revit-2020-structure-example.zip');
  const started = performance.now();
  await page.locator('#file-input').setInputFiles(sample);
  await page.locator('#load-overlay').waitFor({ state: 'hidden', timeout: 180000 });
  await page.waitForFunction(() => document.querySelector('#component-count')?.textContent !== '0' && document.querySelector('#component-count')?.textContent !== '5,000');
  await page.locator('.component-name').first().waitFor();
  await settle(page);
  assert.equal(await page.locator('#load-error').isVisible(), false);
  assert.ok(!/^0 /.test(await page.locator('#model-stats').getAttribute('title')));
  const componentFiles = unzipSync(await readFile(sample), { filter: file => /(^|\/)components\//i.test(file.name) });
  let geometryId, noGeometryId;
  for (const bytes of Object.values(componentFiles)) {
    const data = JSON.parse(strFromU8(bytes));
    const records = Array.isArray(data) ? data : Object.values(data);
    geometryId ??= records.find(record => record && Array.isArray(record.Nodes) && record.Nodes.length)?.Id;
    noGeometryId ??= records.find(record => record && Array.isArray(record.Nodes) && !record.Nodes.length)?.Id;
    if (geometryId && noGeometryId) break;
  }
  assert.ok(geometryId, 'real sample must include a component with exported geometry');
  if (noGeometryId) {
    await page.locator('#search').fill(String(noGeometryId));
    await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '1 个构件');
    await page.waitForFunction(id => document.querySelector('.component-row')?.dataset.id === id, String(noGeometryId));
    await page.locator('.component-name').first().click();
    assert.equal(await page.locator('#section-selection').isDisabled(), true, 'a metadata-only component cannot enable geometry sectioning');
  }
  await page.locator('#search').fill(String(geometryId));
  await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '1 个构件');
  await page.waitForFunction(id => document.querySelector('.component-row')?.dataset.id === id, String(geometryId));
  await page.locator('.component-name').first().click();
  await page.locator('#section-tool').click(); await page.locator('#section-selection').click();
  assert.match(await page.locator('#section-status').textContent(), /剖切已启用/);
  await page.locator('#ghost-mode').check(); await page.locator('#isolate-selection').click();
  await settle(page);
  console.log(JSON.stringify({ realModel: path.basename(sample), components: await page.locator('#component-count').textContent(), seconds: +((performance.now() - started) / 1000).toFixed(2), rendering: await page.locator('#model-stats').getAttribute('title') }));
  await page.screenshot({ path: 'test-results/workbench-real-model.png' });
  await page.locator('#close-model').click(); await settle(page);
  const closed = [];
  for (let cycle = 0; cycle < 3; cycle++) {
    await page.locator('#demo-button').click(); await page.locator('#load-overlay').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '4');
    await page.locator('.component-name').first().click();
    await page.locator('#ghost-mode').check(); await page.locator('#isolate-selection').click();
    await page.locator('#section-tool').click(); await page.locator('#section-selection').click();
    await settle(page); await page.locator('#close-model').click(); await settle(page);
    closed.push(await page.evaluate(() => globalThis.__bimGlSnapshot()));
  }
  assert.deepEqual(closed[1], closed[0], 'closing a second model cycle must return to the first closed GPU resource counts');
  assert.deepEqual(closed[2], closed[0], 'GPU resource counts must remain stable over repeated model cycles');
  assert.equal(await page.locator('canvas').count(), 1);
  assert.deepEqual(requests, [], 'no model data may leave the local viewer');
  assert.deepEqual(errors, [], 'no shader compile or browser runtime errors');
  console.log(JSON.stringify({ result: 'PASS', closedGpuResourceCounts: closed, consoleErrors: errors.length, outsideRequests: requests.length }));
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
