import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

// This entry point only launches a new headless Chromium process. It never
// imports Electron, connects to an existing browser, or sends native input.
delete process.env.PWDEBUG;
const arguments_ = new Set(process.argv.slice(2));
for (const value of arguments_) {
  if (value !== '--baseline-only') throw new Error(`Unsupported option: ${value}`);
}
const baselineOnly = arguments_.has('--baseline-only');
const viewerRoot = fileURLToPath(new URL('../', import.meta.url));
const distRoot = path.join(viewerRoot, 'dist');
const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const runDirectory = path.join(viewerRoot, 'test-results', `background-${new Date().toISOString().replaceAll(/[:.]/g, '-')}-${process.pid}`);
await mkdir(runDirectory, { recursive: true });
const builtIndex = await readFile(path.join(distRoot, 'index.html')).catch(() => {
  throw new Error('Missing dist/index.html. Run npm run build in tools/bim-viewer first.');
});
const report = {
  startedAt: new Date().toISOString(),
  result: 'RUNNING',
  scope: baselineOnly ? 'headless-browser-baseline' : 'headless-browser-navigation-assistant',
  headless: true,
  isolatedBrowserContext: true,
  nativeDesktopInput: false,
  electronStarted: false,
  revitStarted: false,
  modelSource: baselineOnly ? 'Built-in four-component synthetic demo only; no customer model' : 'Built-in demo and adversarial one-component synthetic ZIP; no customer model',
  builtIndexSha256: createHash('sha256').update(builtIndex).digest('hex'),
  ignoredElectronEnvironment: ['BIM_TEST_ELECTRON', 'BIM_TEST_PACKAGED', 'BIM_TEST_USER_DATA'].filter(key => !!process.env[key]),
  checks: [],
  browserErrors: [],
  blockedRequests: [],
  notTested: ['Real Revit export / SDK geometry', 'Native desktop windows / installer / updater', 'Actual external AI provider'],
};
if (baselineOnly) report.notTested.push('Walk / fly navigation', 'Local assistant panel');
const browserEnvironment = { ...process.env };
for (const key of ['PWDEBUG', 'BIM_TEST_ELECTRON', 'BIM_TEST_PACKAGED', 'BIM_TEST_USER_DATA', 'ELECTRON_RUN_AS_NODE']) delete browserEnvironment[key];

function browserPath() {
  if (process.env.BIM_TEST_BROWSER) {
    assert.ok(path.isAbsolute(process.env.BIM_TEST_BROWSER), 'BIM_TEST_BROWSER must be an absolute browser executable path');
    assert.ok(existsSync(process.env.BIM_TEST_BROWSER), 'BIM_TEST_BROWSER does not exist');
    return process.env.BIM_TEST_BROWSER;
  }
  if (existsSync(chromium.executablePath())) return chromium.executablePath();
  if (process.platform === 'win32') {
    for (const candidate of [
      path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Google', 'Chrome', 'Application', 'chrome.exe'),
      path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    ]) if (existsSync(candidate)) return candidate;
  }
  throw new Error('No test Chromium found. Install Playwright Chromium, or set BIM_TEST_BROWSER to a browser executable. No browser was downloaded automatically.');
}

let browser;
let context;
let server;
let page;
let receivedSignal;
function stop(signal) {
  receivedSignal = signal;
  process.exitCode = signal === 'SIGINT' ? 130 : 143;
  // Closing only our own browser also releases any pending page operation.
  void browser?.close().catch(() => {});
}
const onInterrupt = () => stop('SIGINT');
const onTerminate = () => stop('SIGTERM');
process.once('SIGINT', onInterrupt);
process.once('SIGTERM', onTerminate);
async function check(name, test) {
  if (receivedSignal) throw new Error(`Test cancelled by ${receivedSignal}`);
  const started = performance.now();
  await test();
  report.checks.push({ name, result: 'PASS', elapsedMs: Math.round(performance.now() - started) });
  console.log(`PASS ${name}`);
}
const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
async function demo() {
  await page.locator('#demo-button').click();
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '4');
  await settle();
}
async function canvasImage() {
  await settle();
  // Element screenshots include any sibling overlay painted over the element.
  // Mask only those panels so typing or status text cannot fake camera motion.
  // Full-page evidence screenshots remain unmasked.
  return page.locator('#viewport canvas').screenshot({ mask: [page.locator('#tools-panel:visible'), page.locator('#toast:visible')], maskColor: '#000000' });
}
async function viewportClick(fractionX = .5, fractionY = .5) {
  const box = await page.locator('#viewport canvas').boundingBox();
  assert.ok(box, 'a visible rendering canvas is required');
  // Playwright's virtual pointer is scoped to this headless page, not the OS.
  for (const [x, y] of [[fractionX, fractionY], [.12, .8], [.15, .9], [.4, .85], [.1, .1]]) {
    const point = { x: box.x + box.width * x, y: box.y + box.height * y };
    if (await page.evaluate(({ x, y }) => document.elementFromPoint(x, y) === document.querySelector('#viewport canvas'), point)) {
      await page.mouse.click(point.x, point.y);
      return;
    }
  }
  assert.fail('No unobstructed canvas point is available for navigation activation');
}

try {
  server = createServer(async (request, response) => {
    if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405).end(); return; }
    let file;
    try { file = resolveAssetPath(distRoot, new URL(request.url, 'http://127.0.0.1').pathname); }
    catch { response.writeHead(400).end(); return; }
    if (!file) { response.writeHead(404).end(); return; }
    try {
      const bytes = await readFile(file);
      response.writeHead(200, responseHeaders(file));
      response.end(request.method === 'HEAD' ? undefined : bytes);
    } catch { response.writeHead(404).end(); }
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const origin = `http://127.0.0.1:${server.address().port}`;
  if (receivedSignal) throw new Error(`Test cancelled by ${receivedSignal}`);
  browser = await chromium.launch({ executablePath: browserPath(), headless: true, chromiumSandbox: true, env: browserEnvironment, args: ['--enable-unsafe-swiftshader'] });
  report.browserVersion = browser.version();
  context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1, locale: 'zh-CN', serviceWorkers: 'block' });
  await context.route('**/*', route => {
    const request = route.request();
    const local = request.url().startsWith('blob:') || request.url().startsWith('data:') || new URL(request.url()).origin === origin;
    if (!local || !['GET', 'HEAD'].includes(request.method())) {
      // Only record the destination origin: never persist possible query data.
      report.blockedRequests.push({ origin: new URL(request.url()).origin, method: request.method() });
      return route.abort();
    }
    return route.continue();
  });
  page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => report.browserErrors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && !/favicon.*404/.test(message.text())) report.browserErrors.push(message.text());
  });
  await check('offline viewer and synthetic demo render', async () => {
    const response = await page.goto(`${origin}/`);
    assert.ok(response.ok());
    assert.match(response.headers()['content-security-policy'], /connect-src 'self'/);
    await page.locator('#welcome').waitFor({ state: 'visible' });
    await demo();
    assert.equal(await page.locator('#viewport canvas').count(), 1);
    assert.ok(!/^0 /.test(await page.locator('#model-stats').getAttribute('title')), 'demo must submit nonzero GPU draw calls');
  });
  await check('component selection and isolation change real rendered geometry', async () => {
    await page.locator('.component-name').filter({ hasText: '示例墙' }).click();
    assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
    const complete = await canvasImage();
    await page.locator('#isolate-selection').click();
    assert.ok(!complete.equals(await canvasImage()), 'isolation must change the rendered canvas');
    await page.locator('#show-all').click();
    await page.locator('#clear-selection').click();
  });
  if (!baselineOnly) {
    // Kept separate from the baseline so old dist output cannot silently count
    // as verification of new navigation / assistant features.
    const { exerciseNavigationAndAssistant } = await import('../tests/background-features.mjs');
    await exerciseNavigationAndAssistant({ page, check, demo, canvasImage, viewportClick, settle, runDirectory });
  }
  await check('model closes without leaving stale geometry or duplicated canvas', async () => {
    await page.locator('#close-model').click();
    await page.locator('#welcome').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#component-count').textContent(), '0');
    assert.equal(await page.locator('#viewport canvas').count(), 1);
    await demo();
    await page.screenshot({ path: path.join(runDirectory, 'demo.png') });
  });
  await check('no external requests or renderer errors', async () => {
    assert.deepEqual(report.blockedRequests, [], 'model assistance and navigation must not send requests outside this test server');
    assert.deepEqual(report.browserErrors, []);
  });
  report.result = 'PASS';
} catch (error) {
  report.result = receivedSignal ? 'CANCELLED' : 'FAIL';
  if (receivedSignal) report.cancelledBy = receivedSignal;
  report.error = error.stack || String(error);
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(runDirectory, 'failure.png') }).catch(() => {});
  console.error(error.stack || error);
  if (!receivedSignal) process.exitCode = 1;
} finally {
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
  if (server?.listening) await new Promise(resolve => server.close(resolve));
  report.finishedAt = new Date().toISOString();
  await writeFile(path.join(runDirectory, 'report.json'), `${JSON.stringify(report, null, 2)}\n`);
  process.removeListener('SIGINT', onInterrupt);
  process.removeListener('SIGTERM', onTerminate);
  console.log(`${report.result}: ${path.relative(viewerRoot, path.join(runDirectory, 'report.json'))}`);
}
