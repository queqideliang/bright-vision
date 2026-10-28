import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chromium } from 'playwright';
import { strToU8, zipSync } from 'fflate';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('dist');
const output = path.resolve('test-results/quantities');
await mkdir(output, { recursive: true });
const server = createServer(async (req, res) => {
  const file = resolveAssetPath(root, new URL(req.url, 'http://localhost').pathname);
  if (!file) { res.writeHead(404).end(); return; }
  try { const data = await readFile(file); res.writeHead(200, responseHeaders(file)); res.end(data); }
  catch { res.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `http://127.0.0.1:${server.address().port}/`;
const matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const errors = [], outsideRequests = [];
let browser;
let assertions = 0;
function check(value, message) { assert.ok(value, message); assertions++; }
function metric(key, value, status = value === null ? 'missing' : 'available') {
  const definitions = { length: ['m', 'CURVE_ELEM_LENGTH'], area: ['m2', 'HOST_AREA_COMPUTED'], volume: ['m3', 'HOST_VOLUME_COMPUTED'] };
  return { value, status, unit: definitions[key][0], parameter: definitions[key][1] };
}
function snapshot(uniqueId, length, area, volume) {
  return { schemaVersion: 1, source: 'revit-parameters',
    sourceElement: { uniqueId, elementId: uniqueId, documentScope: 'doc', linkInstanceScope: '' },
    metrics: { length: metric('length', length), area: metric('area', area), volume: metric('volume', volume) } };
}
function fixture(records) {
  const levels = {};
  for (const record of records) {
    const level = levels[record.level] ??= { attribute: { elevation: record.level === 'L2' ? 3 : 0 }, elements: {} };
    (level.elements[record.category] ??= []).push({ id: record.id, name: record.name });
  }
  const data = {
    'manifest.json': { units: { length: 'm', source: 'fixture' } },
    'components/components-0-0.json': records.map((record, index) => ({ Id: record.id, name: record.name, type: record.category,
      BuildInCategory: record.category, familyName: 'Test family', familySymbol: 'Test type', matrix, Nodes: [String(index)],
      ...(record.snapshot ? { QuantitySnapshot: record.snapshot } : {}) })),
    'nodes/nodes-0-0.json': Object.fromEntries(records.map((_, index) => [String(index), { geometry: 'g', material: 'm', matrix: [...matrix.slice(0, 12), index * 1.5, 0, 0, 1] }])),
    'geometries/geometries-0-0.json': { g: { type: 'MESH', positions: [-.4, 0, 0, .4, 0, 0, 0, 0, 1], indices: [0, 1, 2] } },
    'materials/materials-0-0.json': { m: { color: [70, 140, 210], opacity: 1 } },
    'LevelInfo.json': levels,
  };
  return zipSync(Object.fromEntries(Object.entries(data).map(([name, value]) => [name, strToU8(JSON.stringify(value))])));
}
async function open(page, bytes, name, count) {
  await page.locator('#file-input').setInputFiles({ name, mimeType: 'application/zip', buffer: Buffer.from(bytes) });
  await page.locator('#load-overlay').waitFor({ state: 'hidden', timeout: 120000 });
  await page.waitForFunction(expected => document.querySelector('#component-count')?.textContent === String(expected), count);
  await page.locator('#quantity-tool').click();
  await page.locator('#quantity-controls').waitFor({ state: 'visible' });
}
async function total(page, key) {
  const raw = await page.locator(`#quantity-totals [data-metric="${key}"] strong`).textContent();
  return raw === '—' ? null : Number(raw.replaceAll(',', ''));
}
async function csvDownload(page, name) {
  const event = page.waitForEvent('download');
  await page.locator('#quantity-export').click();
  const download = await event;
  const file = path.join(output, name);
  await download.saveAs(file);
  const data = await readFile(file);
  check(data[0] === 0xef && data[1] === 0xbb && data[2] === 0xbf, 'CSV must use UTF-8 BOM');
  return data.toString('utf8');
}
try {
  browser = await chromium.launch({ executablePath: process.env.BIM_TEST_BROWSER || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    headless: true, args: ['--enable-unsafe-swiftshader'] });
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
  const exported = await readFile('../bright-revit-exporter/artifacts/BV-1.4.0-final/export-fixture.zip');
  await open(page, exported, 'BV-1.4.0-fixture.zip', 2);
  check(await total(page, 'length') === 3.048, 'C# length must stay metres');
  check(await total(page, 'area') === 11.61288, 'C# square-foot conversion must reach the browser');
  check(Math.abs(await total(page, 'volume') - .226535) < .0000001, 'C# volume must round only at display');
  check(await page.locator('#quantity-body tr').count() === 2, 'component rows must render');
  await page.locator('#quantity-body .quantity-focus').first().click();
  check(await page.locator('.id-row code').textContent() === 'fixture-1', 'schedule click must select the source component');
  await page.locator('#quantity-scope').selectOption('selection');
  check(await total(page, 'volume') === 0, 'available zero must not be shown as missing');
  const csv = await csvDownload(page, 'source-selection.csv');
  check(csv.includes('"fixture-1"') && !csv.includes('"fixture-2"'), 'CSV must use selection scope');
  check(csv.includes('"3.048"') && csv.includes('"0"'), 'CSV must retain SI values and zero');
  await page.locator('#language-toggle').click();
  check(await page.locator('#tools-title').textContent() === 'Quantity schedule', 'tool must support English');
  check((await page.locator('#quantity-head').textContent()).includes('Area (m²)'), 'metric headings must translate');
  await page.screenshot({ path: path.join(output, 'source-selection-en.png') });

  await page.locator('#close-model').click();
  const bad = snapshot('bad', 1, 2, null); bad.metrics.area.unit = 'm'; bad.metrics.volume.status = 'unavailable';
  const mixed = fixture([
    { id: 'scene-a', name: '=1+1 <img src=x onerror="window.__quantityUnsafe=true">', category: 'Walls', level: 'L1', snapshot: snapshot('same-wall', 3, 6, 1) },
    { id: 'scene-b', name: 'Wall second piece', category: 'Walls', level: 'L2', snapshot: snapshot('same-wall', 4, 6, 1) },
    { id: 'scene-c', name: 'Door', category: 'Doors', level: 'L1', snapshot: snapshot('door', null, 2, 0) },
    { id: 'scene-d', name: 'Bad area', category: 'Generic', level: 'L1', snapshot: bad },
    { id: 'scene-e', name: 'Old package object', category: 'Generic', level: 'L1' },
  ]);
  await open(page, mixed, 'mixed.zip', 5);
  // Scope controls persist across loads; set them explicitly for this case.
  await page.locator('#quantity-scope').selectOption('filtered');
  check(await total(page, 'length') === 1, 'conflicting source length must be excluded');
  check(await total(page, 'area') === 8, 'bad units must be excluded while valid source areas remain');
  check(await total(page, 'volume') === 1, 'duplicate scene pieces must contribute once');
  check((await page.locator('#quantity-coverage').textContent()).startsWith('4 quantity objects · 5 model records'), 'object count must distinguish duplicate records');
  check(await page.locator('#quantity-body img').count() === 0 && await page.evaluate(() => window.__quantityUnsafe) === undefined, 'model names must remain text');
  const allCsv = await csvDownload(page, 'mixed-en.csv');
  check(allCsv.includes("'=1+1 <img"), 'CSV names must be protected from formula injection');
  await page.locator('#level-filter').selectOption('L1');
  await page.waitForFunction(() => document.querySelector('#quantity-controls')?.getAttribute('aria-busy') === 'false');
  check(await total(page, 'length') === 1, 'level filtering must not hide the other source piece conflict');
  check((await page.locator('#quantity-body').textContent()).includes('Multiple levels (not split)'), 'cross-level sources must not acquire a false single-level attribution');
  await page.locator('#quantity-body .quantity-focus').first().click();
  await page.locator('#quantity-scope').selectOption('selection');
  check(await total(page, 'length') === null, 'source conflict must remain invalid in selection scope');
  check((await page.locator('#quantity-coverage').textContent()).startsWith('1 quantity objects · 2 model records'), 'selection must retain every scene piece of one source');
  await page.locator('#quantity-mode').selectOption('level');
  check(await page.locator('#quantity-body tr').count() === 1, 'cross-level whole-source quantity belongs to one group');
  await page.screenshot({ path: path.join(output, 'conflicting-source-en.png') });
  await page.locator('#quantity-scope').selectOption('filtered');
  await page.locator('#hide-selection').click();
  check(await total(page, 'area') === 8, 'hiding must not recalculate quantities in filter scope');
  await page.locator('#quantity-scope').selectOption('selection');
  check(await total(page, 'area') === null, 'current selection scope follows the selection cleared by hiding');

  await page.locator('#close-model').click();
  await open(page, fixture([{ id: 'old', name: 'Legacy', category: 'Walls', level: 'L1' }]), 'old.zip', 1);
  await page.locator('#quantity-scope').selectOption('filtered');
  check(await total(page, 'volume') === null, 'old packages must not get fabricated zero quantities');
  check((await page.locator('#quantity-missing').textContent()).includes('BV 1.4.0'), 'old packages must explain how to get snapshots');
  await page.locator('#language-toggle').click();
  check((await page.locator('#quantity-missing').textContent()).includes('缺失量不会按零计算'), 'missing state must translate to Chinese');

  await page.locator('#close-model').click();
  await open(page, fixture(Array.from({ length: 55 }, (_, index) => ({ id: `page-${index}`, name: `Page ${index + 1}`, category: 'Walls', level: 'L1', snapshot: snapshot(`page-${index}`, 1, 2, 3) }))), 'pages.zip', 55);
  await page.locator('#quantity-mode').selectOption('component');
  check(await page.locator('#quantity-body tr').count() === 50, 'quantity DOM rows must be paged');
  await page.locator('#quantity-next').click();
  check(await page.locator('#quantity-body tr').count() === 5, 'last page must contain the remaining rows');
  const pagedCsv = await csvDownload(page, 'all-pages.csv');
  check(pagedCsv.includes('"Page 1"') && pagedCsv.includes('"Page 55"'), 'CSV must include the whole scope, not just the current page');
  check(await total(page, 'volume') === 165, 'pagination must not change the aggregate');
  await page.locator('#quantity-mode').selectOption('category');
  check(await page.locator('#quantity-body tr').count() === 1, 'category grouping must work');
  check(await total(page, 'area') === 110, 'category grouping must not change quantity totals');
  await page.screenshot({ path: path.join(output, 'quantity-category-zh.png') });
  check(errors.length === 0, `browser errors: ${errors.join('; ')}`);
  check(outsideRequests.length === 0, 'model workflow must not request external services');
  await writeFile(path.join(output, 'report.json'), JSON.stringify({ result: 'PASS', assertions, errors, outsideRequests,
    source: 'Frozen C# synthetic export fixture and browser fixtures; no live Revit export', headless: true }, null, 2));
  console.log(JSON.stringify({ result: 'PASS', assertions, output, liveRevitExportVerified: false }));
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
