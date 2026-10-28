import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { parseModelZip } from '../src/import/parse.ts';
import { createQuantityIndex, QUANTITY_KEYS } from '../src/quantities.ts';

const hostFile = path.resolve(process.argv[2] || '');
if (!process.argv[2]) throw new Error('Usage: node --experimental-strip-types tests/revit-host-browser.mjs <host-report.json>');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (left, right) => process.platform === 'win32'
  ? path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase() : path.resolve(left) === path.resolve(right);
const hostBytes = await readFile(hostFile);
const host = JSON.parse(hostBytes.toString('utf8').replace(/^\ufeff/u, ''));
assert.equal(host.result, 'PASS');
const dataValidation = JSON.parse(await readFile(path.join(path.dirname(hostFile), 'host-data-validation.json'), 'utf8'));
assert.equal(dataValidation.result, 'PASS', 'independent native data validation must pass first');
assert.equal(dataValidation.runId, host.runId, 'data validation belongs to a different host run');
assert.ok(samePath(dataValidation.hostReport.path, hostFile), 'data validation belongs to a different host report path');
assert.equal(dataValidation.hostReport.sha256, sha256(hostBytes), 'host report changed after independent data validation');
const modelBytes = await readFile(host.export.path);
assert.ok(samePath(dataValidation.zip.path, host.export.path), 'data validation belongs to a different ZIP path');
assert.equal(dataValidation.zip.sha256, sha256(modelBytes), 'ZIP changed after independent data validation');
assert.equal(dataValidation.zip.bytes, modelBytes.byteLength, 'ZIP size differs from the independently validated input');
const model = await parseModelZip(modelBytes, path.basename(host.export.path));
const index = createQuantityIndex(model.components);
const totals = index.summarize(model.components);
const root = path.resolve('dist');
const output = path.join(path.dirname(hostFile), 'browser');
await mkdir(output, { recursive: true });
const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const server = createServer(async (req, res) => {
  const file = resolveAssetPath(root, new URL(req.url, 'http://localhost').pathname);
  if (!file) { res.writeHead(404).end(); return; }
  try { res.writeHead(200, responseHeaders(file)); res.end(await readFile(file)); }
  catch { res.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
const errors = [], outsideRequests = [];
let browser;
let assertions = 0;
function check(value, message) { assert.ok(value, message); assertions++; }
async function checkCoverage(page, summary) {
  const expected = `${summary.count.toLocaleString('zh-CN')} 个计量对象 · ${summary.recordCount.toLocaleString('zh-CN')} 条模型记录 · 合并 ${summary.duplicateSources.toLocaleString('zh-CN')} 条重复来源记录`;
  check(await page.locator('#quantity-coverage').textContent() === expected, 'schedule coverage differs from the validated ZIP source-object counts');
}
async function checkTotals(page) {
  for (const key of QUANTITY_KEYS) {
    const label = await page.locator(`#quantity-totals [data-metric="${key}"] strong`).textContent();
    if (totals.metrics[key].value === null) check(label === '—', `${key}: unavailable must stay unavailable`);
    else check(Math.abs(Number(label.replaceAll(',', '')) - totals.metrics[key].value) <= Math.max(0.000001, Math.abs(totals.metrics[key].value) * 1e-12), `${key}: UI total differs from the validated ZIP quantity-index total`);
  }
}
try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', headless: true,
    args: ['--enable-unsafe-swiftshader'] });
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, acceptDownloads: true });
  await context.route('**/*', route => {
    const address = route.request().url();
    if (!address.startsWith(url) && !address.startsWith('blob:') && !address.startsWith('data:')) {
      outsideRequests.push(address); return route.abort();
    }
    return route.continue();
  });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', event => { if (event.type() === 'error' && !/favicon/i.test(event.text())) errors.push(event.text()); });
  await page.goto(url);
  await page.locator('#file-input').setInputFiles({ name: path.basename(host.export.path), mimeType: 'application/zip', buffer: modelBytes });
  await page.locator('#load-overlay').waitFor({ state: 'hidden', timeout: 120000 });
  await page.waitForFunction(expected => document.querySelector('#component-count')?.textContent === expected,
    model.components.length.toLocaleString('zh-CN'), { timeout: 120000 });
  check(await page.locator('#load-error').isVisible() === false, 'actual model must load without import error');
  await page.screenshot({ path: path.join(output, 'real-model.png') });
  await page.locator('#quantity-tool').click();
  await page.locator('#quantity-mode').selectOption('category');
  await checkTotals(page);
  check(await page.locator('#quantity-body tr').count() > 0, 'real model category schedule must render');
  await checkCoverage(page, totals);
  await page.screenshot({ path: path.join(output, 'real-quantities-zh.png') });
  const firstRow = index.buildRows(model.components, 'category')[0];
  await page.locator('#quantity-body .quantity-focus').first().click();
  if (firstRow.componentIds.length === 1) check(await page.locator('.id-row code').textContent() === firstRow.componentIds[0], 'schedule must select its component');
  else check(await page.locator('#properties .multi-selection h3').textContent() === `已选 ${firstRow.componentIds.length.toLocaleString('zh-CN')} 个构件`, 'schedule group must select its exact scene record count');
  check(await page.locator('#selection-summary').textContent() === `已选 ${firstRow.componentIds.length.toLocaleString('zh-CN')} 个构件 · Ctrl 点击多选`, 'selected scene record count must match exactly');
  await page.locator('#quantity-scope').selectOption('selection');
  const expectedSelection = index.summarize(firstRow.componentIds.map(id => model.components.find(component => component.id === id)));
  await checkCoverage(page, expectedSelection);
  await page.locator('#quantity-scope').selectOption('filtered');
  const downloadEvent = page.waitForEvent('download');
  await page.locator('#quantity-export').click();
  const download = await downloadEvent;
  const csvPath = path.join(output, 'real-browser-quantities.csv');
  await download.saveAs(csvPath);
  const csv = await readFile(csvPath);
  check(csv[0] === 0xef && csv[1] === 0xbb && csv[2] === 0xbf, 'actual browser CSV must have UTF-8 BOM');
  check(csv.toString('utf8').includes('Revit') && csv.toString('utf8').includes('m3'), 'actual browser CSV must preserve source and SI units');
  await page.locator('#language-toggle').click();
  check(await page.locator('#tools-title').textContent() === 'Quantity schedule', 'actual model workflow must support English');
  await checkTotals(page);
  await page.screenshot({ path: path.join(output, 'real-quantities-en.png') });
  check(errors.length === 0, `browser errors: ${errors.join('; ')}`);
  check(outsideRequests.length === 0, 'actual model must stay local');
  const report = { result: 'PASS', assertions, runId: host.runId, model: model.name, components: model.components.length,
    quantityObjects: totals.count, totals: totals.metrics, csvPath, errors, outsideRequests,
    inputKind: 'independently-validated-revit-host-zip', hostReportSha256: sha256(hostBytes), zipSha256: sha256(modelBytes),
    uiMatchesValidatedZipSchedule: true,
    nativeReferenceCoverage: { matchedRootSources: dataValidation.quantities.matchedRootSources,
      unmatchedRootSources: dataValidation.quantities.unmatchedRootSources,
      linkedSourcesNotIndependentlyChecked: dataValidation.quantities.linkedOrOtherScopeSourcesNotIndependentlyChecked },
    limitations: ['UI totals are compared with the production quantity index over the independently validated ZIP.',
      'Native parameter checks cover the matched root-document references recorded by the separate data validator; unmatched and linked sources remain outside that coverage.'],
    headless: true, revitRibbonClickVerified: false, nativeViewerWindowVerified: false };
  await writeFile(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
