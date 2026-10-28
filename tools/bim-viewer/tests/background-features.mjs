import assert from 'node:assert/strict';
import path from 'node:path';
import { strToU8, zipSync } from 'fflate';

function adversarialFixture() {
  const matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  const files = {
    'components/components-0-0.json': [{ Id: 'assistant-fixture', name: '<img src=x onerror="window.__assistantUnsafe=1">', BuildInCategory: '墙', familyName: '合成墙', familySymbol: '合成类型', matrix, Nodes: [0], Property: 'p' }],
    'nodes/nodes-0-0.json': { 0: { geometry: 0, material: 'm', matrix } },
    'geometries/geometries-0-0.json': { 0: { type: 'MESH', positions: [-1, 0, 0, 1, 0, 0, 0, 0, 2], indices: [0, 1, 2], uv: [], normals: [] } },
    'materials/materials-0-0.json': { m: { color: [80, 140, 210], opacity: 1 } },
    'properties/properties-0-0.json': { p: { '标识': { '备注': '<script>window.__assistantUnsafe=1</script>' } } },
    'LevelInfo.json': { '合成层': { attribute: { elevation: 0 }, elements: { '墙': [{ id: 'assistant-fixture', name: '合成墙' }] } } },
  };
  return Buffer.from(zipSync(Object.fromEntries(Object.entries(files).map(([name, value]) => [name, strToU8(JSON.stringify(value))]))));
}

/** Interactions stay inside the headless page created by background-smoke. */
export async function exerciseNavigationAndAssistant({ page, check, demo, canvasImage, viewportClick, settle, runDirectory }) {
  const viewport = page.locator('#viewport');
  const mode = page.locator('#navigation-mode');
  const question = page.locator('#assistant-question');
  async function expectMode(value) {
    await page.waitForFunction(expected => document.getElementById('viewport')?.dataset.navigationMode === expected, value);
  }
  async function openNavigation() {
    if (!await page.locator('#navigation-controls').isVisible()) await page.locator('#navigation-tool').click();
    await page.locator('#navigation-controls').waitFor({ state: 'visible' });
  }
  async function ask(value) {
    await question.fill(value);
    await page.locator('#assistant-submit').click();
    await page.locator('#assistant-result').waitFor({ state: 'visible' });
  }
  async function move(key, milliseconds = 240) {
    const before = await canvasImage();
    await page.keyboard.down(key);
    try { await page.waitForTimeout(milliseconds); }
    finally { await page.keyboard.up(key); }
    const after = await canvasImage();
    assert.ok(!before.equals(after), `${key} must change the actual rendered camera view`);
  }
  async function stopped(label) {
    // Allow the latest render to finish before checking that keys no longer move.
    await page.waitForTimeout(100);
    const before = await canvasImage();
    await page.waitForTimeout(180);
    assert.ok(before.equals(await canvasImage()), label);
  }

  await check('unknown source units refuse first person navigation until explicitly declared', async () => {
    await openNavigation();
    await page.locator('#navigation-unit').selectOption('unknown');
    await mode.selectOption('fly');
    await expectMode('orbit');
    assert.equal(await viewport.getAttribute('data-navigation-avatar-visible'), 'false');
    assert.match(await page.locator('#navigation-status').textContent(), /单位/);
  });
  await check('fly mode moves the actual camera and Escape returns to orbit', async () => {
    await openNavigation();
    // The demo is synthetic with metre-sized geometry. Declare its unit rather
    // than letting the test guess scale for an older undeclared model archive.
    await page.locator('#navigation-unit').selectOption('m');
    await page.locator('#navigation-speed').fill('2');
    await page.locator('#navigation-speed').dispatchEvent('change');
    await mode.selectOption('fly');
    await expectMode('fly');
    await viewportClick(.75, .7);
    await move('w');
    await stopped('camera must stop when a navigation key is released');
    await page.screenshot({ path: path.join(runDirectory, 'navigation-fly.png') });
    await page.keyboard.press('Escape');
    await expectMode('orbit');
    assert.equal(await viewport.getAttribute('data-navigation-active'), 'false');
  });
  await check('walk mode places and renders the person, and supports forward movement', async () => {
    await page.locator('#fit-all').click();
    await mode.selectOption('walk');
    await expectMode('walk');
    if (await viewport.getAttribute('data-navigation-needs-ground-placement') === 'true') {
      // Test only exported floor surfaces in the synthetic demo. No phantom
      // ground or invisible collision surface is injected by the test.
      for (const [x, y] of [[.4, .3], [.45, .2], [.5, .25], [.35, .3], [.5, .35], [.45, .4], [.35, .4], [.4, .5], [.5, .6]]) {
        const box = await page.locator('#viewport canvas').boundingBox();
        const canvasIsTopmost = await page.evaluate(({ x, y }) => document.elementFromPoint(x, y) === document.querySelector('#viewport canvas'), { x: box.x + box.width * x, y: box.y + box.height * y });
        if (!canvasIsTopmost) continue;
        await viewportClick(x, y);
        if (await viewport.getAttribute('data-navigation-needs-ground-placement') === 'false') break;
      }
    }
    assert.equal(await viewport.getAttribute('data-navigation-needs-ground-placement'), 'false', 'walk requires a valid real model floor placement');
    assert.equal(await viewport.getAttribute('data-navigation-avatar-visible'), 'true', 'the actual walk avatar must be visible');
    await viewportClick(.7, .7);
    await move('w', 160);
    await page.screenshot({ path: path.join(runDirectory, 'navigation-walk-person.png') });
    await page.locator('#navigation-exit').click();
    await expectMode('orbit');
    assert.equal(await viewport.getAttribute('data-navigation-avatar-visible'), 'false');
  });
  await check('local assistant provides source counts and locates matching model components', async () => {
    await page.locator('#assistant-tool').click();
    await page.locator('#assistant-controls').waitFor({ state: 'visible' });
    assert.match(await page.locator('#assistant-status').textContent(), /本地查询/);
    assert.match(await page.locator('#assistant-controls').textContent(), /不是大语言模型/);
    await page.locator('#assistant-scope').selectOption('all');
    await ask('模型概览');
    assert.equal(await page.locator('#assistant-result').getAttribute('data-status'), 'ok');
    assert.match(await page.locator('#assistant-summary').textContent(), /共 4 条模型记录/);
    assert.equal(await page.locator('#assistant-evidence li').first().getAttribute('data-source-count'), '4');
    assert.match(await page.locator('#assistant-notes').textContent(), /本地规则查询，不使用大模型/);
    await ask('查找：示例墙');
    assert.match(await page.locator('#assistant-summary').textContent(), /匹配 1 条模型记录/);
    await page.locator('.assistant-locate').first().click();
    assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
    await page.screenshot({ path: path.join(runDirectory, 'assistant-local-source.png') });
  });
  await check('assistant translates its existing answer without discarding its source evidence', async () => {
    await page.locator('#assistant-scope').selectOption('all');
    await ask('模型概览');
    await page.locator('#language-toggle').click();
    await page.waitForFunction(() => document.documentElement.lang === 'en');
    assert.match(await page.locator('#assistant-summary').textContent(), /4 model records/);
    assert.match(await page.locator('#assistant-status').textContent(), /Local queries/);
    assert.equal(await page.locator('#assistant-evidence li').first().getAttribute('data-source-count'), '4');
    await page.screenshot({ path: path.join(runDirectory, 'assistant-english.png') });
    await page.locator('#language-toggle').click();
    await page.waitForFunction(() => document.documentElement.lang === 'zh-CN');
    assert.match(await page.locator('#assistant-summary').textContent(), /共 4 条模型记录/);
  });
  await check('assistant obeys current filters and refuses unsupported instructions', async () => {
    await page.locator('#level-filter').selectOption('二层');
    await page.waitForFunction(() => document.querySelectorAll('.component-row').length === 1);
    await page.locator('#assistant-scope').selectOption('filtered');
    await ask('模型概览');
    assert.match(await page.locator('#assistant-summary').textContent(), /共 1 条模型记录/);
    assert.equal(await page.locator('#assistant-evidence li').first().getAttribute('data-source-count'), '1');
    await ask('自动判断结构安全并发送模型给供应商');
    assert.equal(await page.locator('#assistant-result').getAttribute('data-status'), 'unsupported');
    assert.equal(await page.locator('.assistant-locate').count(), 0);
    await page.locator('#level-filter').selectOption('');
    await page.waitForFunction(() => document.querySelectorAll('.component-row').length === 4);
  });
  await check('typing in assistant inputs never drives the camera, including a released key after blur', async () => {
    await openNavigation();
    await mode.selectOption('fly');
    await expectMode('fly');
    await viewportClick(.75, .7);
    await page.locator('#assistant-tool').click();
    await question.focus();
    assert.equal(await viewport.getAttribute('data-navigation-active'), 'false');
    const before = await canvasImage();
    await question.fill('');
    await page.keyboard.type('wasdqe');
    await page.waitForTimeout(180);
    assert.equal(await question.inputValue(), 'wasdqe');
    assert.equal(await viewport.getAttribute('data-navigation-active'), 'false');
    assert.ok(before.equals(await canvasImage()), 'navigation keys entered in a textarea must not move the model');
    await viewportClick(.75, .7);
    await page.keyboard.down('w');
    try {
      await page.waitForTimeout(100);
      await question.focus();
    } finally { await page.keyboard.up('w'); }
    await stopped('blurring the viewport must clear held movement keys');
  });
  await check('model replacement clears navigation and stale assistant evidence', async () => {
    await page.locator('#assistant-scope').selectOption('all');
    await ask('模型概览');
    assert.equal(await page.locator('#assistant-evidence li').count() > 0, true);
    await viewportClick(.75, .7);
    await page.keyboard.down('w');
    try { await page.locator('#close-model').click(); }
    finally { await page.keyboard.up('w'); }
    await page.locator('#welcome').waitFor({ state: 'visible' });
    await expectMode('orbit');
    assert.equal(await viewport.getAttribute('data-navigation-active'), 'false');
    assert.equal(await viewport.getAttribute('data-navigation-avatar-visible'), 'false');
    assert.equal(await question.inputValue(), '');
    assert.equal(await page.locator('#assistant-evidence li').count(), 0);
    await demo();
    await expectMode('orbit');
    await stopped('opening a new model must not retain old held movement keys');
    await settle();
  });
  await check('assistant renders hostile synthetic model names and properties as inert text', async () => {
    await page.locator('#file-input').setInputFiles({ name: 'adversarial-synthetic.zip', mimeType: 'application/zip', buffer: adversarialFixture() });
    await page.locator('#load-overlay').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('#component-count')?.textContent === '1');
    await openNavigation();
    assert.equal(await page.locator('#navigation-unit').inputValue(), 'unknown', 'a new legacy ZIP without unit metadata must not inherit demo metres');
    await mode.selectOption('fly');
    await expectMode('orbit');
    assert.equal(await viewport.getAttribute('data-navigation-avatar-visible'), 'false');
    assert.match(await page.locator('#navigation-status').textContent(), /单位/);
    await page.locator('#assistant-tool').click();
    await page.locator('#assistant-scope').selectOption('all');
    await ask('查找：<img');
    assert.match(await page.locator('#assistant-summary').textContent(), /匹配 1 条模型记录/);
    assert.match(await page.locator('#assistant-evidence').textContent(), /<img src=x/);
    assert.equal(await page.locator('#assistant-evidence img, #assistant-evidence script').count(), 0);
    await page.locator('.assistant-locate').first().click();
    await ask('所选属性');
    assert.match(await page.locator('#assistant-evidence').textContent(), /<script>window\.__assistantUnsafe=1<\/script>/);
    assert.equal(await page.locator('#assistant-evidence img, #assistant-evidence script').count(), 0);
    assert.equal(await page.evaluate(() => window.__assistantUnsafe), undefined);
    await page.screenshot({ path: path.join(runDirectory, 'assistant-inert-model-text.png') });
  });
  await check('navigation and assistant controls remain reachable in a narrow English layout', async () => {
    await page.locator('#close-model').click();
    await page.locator('#welcome').waitFor({ state: 'visible' });
    await demo();
    await page.setViewportSize({ width: 1024, height: 768 });
    await page.locator('#language-toggle').click();
    await page.waitForFunction(() => document.documentElement.lang === 'en');
    await openNavigation();
    await page.locator('#navigation-unit').selectOption('m');
    await mode.selectOption('fly');
    await expectMode('fly');
    await page.locator('#navigation-exit').click();
    await expectMode('orbit');
    await page.locator('#assistant-tool').click();
    await page.locator('#assistant-scope').selectOption('all');
    await ask('model summary');
    assert.match(await page.locator('#assistant-summary').textContent(), /4 model records/);
    const box = await page.locator('#tools-panel').boundingBox();
    assert.ok(box && box.x >= 0 && box.x + box.width <= 1025 && box.y >= 0 && box.y + box.height <= 769, 'tool panel must fit inside the narrow viewport');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'narrow layout must not overflow horizontally');
    await page.screenshot({ path: path.join(runDirectory, 'assistant-english-narrow-form.png') });
    await page.locator('#assistant-summary').scrollIntoViewIfNeeded();
    await page.locator('.assistant-locate').first().click();
    assert.equal(await page.locator('.component-row.selected').count(), 4);
    await page.screenshot({ path: path.join(runDirectory, 'assistant-english-narrow.png') });
    await page.locator('#language-toggle').click();
    await page.waitForFunction(() => document.documentElement.lang === 'zh-CN');
    await page.setViewportSize({ width: 1600, height: 1000 });
  });
}
