import assert from 'node:assert/strict';
import { zipSync, strToU8 } from 'fflate';

const settle = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
async function waitReady(page, count) {
  await page.locator('#load-overlay').waitFor({ state: 'hidden' });
  await page.waitForFunction(value => document.querySelector('#component-count')?.textContent === value, count);
  await page.locator('.component-name').first().waitFor();
  await settle(page);
}

/** Runs in a disposable Playwright context with the synthetic demo already open. */
export async function exerciseWorkbench(page) {
  const wall = page.locator('.component-name').filter({ hasText: '示例墙' });
  await wall.click();
  await page.locator('.component-name').filter({ hasText: '示例柱' }).click({ modifiers: ['Control'] });
  assert.match(await page.locator('.multi-selection').textContent(), /已选 2 个构件/);
  await page.locator('#hide-selection').click();
  assert.match(await page.locator('#footer-status').textContent(), /2/);
  await page.locator('#show-all').click();
  await wall.click();
  await page.locator('#ghost-mode').check();
  await page.locator('#isolate-selection').click();
  await settle(page);
  const ghosted = await page.locator('canvas').screenshot();
  await page.locator('#ghost-mode').uncheck();
  await page.locator('#isolate-selection').click();
  await settle(page);
  assert.ok(!ghosted.equals(await page.locator('canvas').screenshot()), 'ghosted context must render differently from fully hidden context');
  await page.locator('#ghost-mode').check();
  await page.locator('#isolate-selection').click();
  await page.locator('#section-tool').click();
  await page.locator('#section-selection').click();
  assert.match(await page.locator('#section-status').textContent(), /剖切已启用/);
  const min = Number(await page.locator('#section-min-x').inputValue());
  const max = Number(await page.locator('#section-max-x').inputValue());
  assert.ok(Number.isFinite(min) && max > min);
  await page.locator('#section-max-x').fill(String((min + max) / 2));
  await page.locator('#section-apply').click();
  await settle(page);
  const clipped = await page.locator('canvas').screenshot();
  await page.locator('#section-clear').click();
  await settle(page);
  assert.ok(!clipped.equals(await page.locator('canvas').screenshot()), 'section planes must change actual rendered pixels');
  await page.locator('#section-selection').click();
  const savedMax = await page.locator('#section-max-x').inputValue();
  await page.locator('#views-tool').click();
  const name = `回归视点 ${Date.now()}`;
  await page.locator('#viewpoint-name').fill(name);
  await page.locator('#viewpoint-save').click();
  assert.equal(await page.locator('#viewpoint-select option').last().textContent(), name);
  await page.locator('#tools-close').click();
  await page.locator('#show-all').click();
  await page.locator('#clear-selection').click();
  await page.locator('#fit-all').click();
  await page.locator('#views-tool').click();
  await page.locator('#viewpoint-restore').click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  assert.equal(await page.locator('#ghost-mode').isChecked(), true);
  assert.match(await page.locator('#footer-status').textContent(), /3/);
  assert.equal(await page.locator('#section-max-x').inputValue(), savedMax);
  await page.locator('#tools-close').click();
  await page.locator('#close-model').click();
  await page.locator('#demo-button').click();
  await waitReady(page, '4');
  await page.locator('#views-tool').click();
  assert.ok((await page.locator('#viewpoint-select option').allTextContents()).includes(name), 'viewpoint must persist for a reopened matching model');
  await page.locator('#viewpoint-select').selectOption({ label: name });
  await page.locator('#viewpoint-restore').click();
  assert.equal(await page.locator('.id-row code').textContent(), 'demo-wall');
  await page.locator('#section-tool').click(); await page.locator('#section-clear').click();
  await page.locator('#measurement-tool').click();
  assert.equal(await page.locator('#source-unit').inputValue(), 'unknown', 'the synthetic/legacy model must not silently assume meters');
  await page.locator('#measurement-start').click();
  const canvas = await page.locator('canvas').boundingBox();
  let measured = false;
  for (const [x, y] of [[.32, .42], [.42, .48], [.29, .56], [.38, .61], [.2, .5], [.45, .55], [.35, .52]]) {
    await page.mouse.click(canvas.x + canvas.width * x, canvas.y + canvas.height * y);
    const result = await page.locator('#measurement-result').textContent();
    if (/\d[\d,.]* 模型单位/.test(result)) { measured = true; break; }
  }
  assert.ok(measured, 'two surface clicks must produce an actual distance in explicit model units');
  const rawDistance = Number((await page.locator('#measurement-result').textContent()).match(/[\d,.]+/)[0].replaceAll(',', ''));
  assert.ok(rawDistance > 0, 'distinct surface picks must have positive distance');
  await page.locator('#source-unit').selectOption('m');
  await page.locator('#measurement-unit').selectOption('mm');
  assert.match(await page.locator('#measurement-result').textContent(), /mm$/);
  const millimeters = Number((await page.locator('#measurement-result').textContent()).match(/[\d,.]+/)[0].replaceAll(',', ''));
  assert.ok(Math.abs(millimeters - rawDistance * 1000) < 0.001, 'declared meters must convert to millimeters without relabeling raw values');
  await page.locator('#measurement-stop').click(); await page.locator('#measurement-clear').click();
  assert.equal(await page.locator('#measurement-result').textContent(), '尚未测量');
  await page.locator('#audit-tool').click();
  assert.equal(await page.locator('.category-stat').count(), 3);
  await page.locator('.category-stat').filter({ hasText: '楼板' }).click();
  await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '2 个构件');
  assert.equal(await page.locator('.component-row').count(), 2);
  await page.locator('#category-filter').selectOption('');
  await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '4 个构件');
  await page.locator('#tools-close').click();
  await page.locator('#ghost-mode').uncheck(); await page.locator('#show-all').click();
  await page.locator('#clear-selection').click(); await page.locator('#fit-all').click();
  return { multiSelection: true, ghost: true, section: true, measurements: true, persistedViewpoint: true, categoryAudit: true };
}

export function repeatedGeometryFixture(count = 5000) {
  const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  const components = [];
  for (let index = 0; index < count; index++) {
    const id = `component-${String(index).padStart(5, '0')}`;
    const matrix = [...identity]; matrix[12] = (index % 100) * 2; matrix[14] = Math.floor(index / 100) * 2;
    components.push({ Id: id, name: `构件 ${index}`, BuildInCategory: '测试类别', familyName: '测试族', familySymbol: '复用类型', matrix, Nodes: [0] });
  }
  const json = {
    'manifest.json': { schemaVersion: 1, lengthUnit: 'm' },
    'components/components-0-0.json': components,
    'nodes/nodes-0-0.json': { 0: { geometry: 0, material: 'm', matrix: identity } },
    'geometries/geometries-0-0.json': { 0: { type: 'MESH', positions: [0, 0, 0, 1, 0, 0, 0, 0, 1], indices: [0, 1, 2], uv: [], normals: [] } },
    'materials/materials-0-0.json': { m: { color: [100, 145, 210], opacity: 1 } },
  };
  return zipSync(Object.fromEntries(Object.entries(json).map(([name, value]) => [name, strToU8(JSON.stringify(value))])));
}

export async function exerciseVirtualTree(page) {
  await page.locator('#file-input').setInputFiles({ name: 'repeated-geometry.zip', mimeType: 'application/zip', buffer: Buffer.from(repeatedGeometryFixture()) });
  await waitReady(page, '5,000');
  assert.equal(await page.locator('#source-unit').inputValue(), 'm', 'manifest length unit must reach the measurement UI');
  assert.ok(await page.locator('.component-row').count() < 60, 'virtual tree must bound DOM rows for 5,000 components');
  await page.locator('#component-list').evaluate(list => { list.scrollTop = list.scrollHeight; });
  await settle(page);
  assert.ok((await page.locator('.component-row').last().getAttribute('data-id')).endsWith('04999'));
  await page.locator('#search').fill('component-04999');
  await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '1 个构件');
  assert.equal(await page.locator('.component-row').count(), 1);
  await page.locator('.component-name').click();
  assert.equal(await page.locator('.id-row code').textContent(), 'component-04999');
  await page.locator('#search').fill('');
  await page.waitForFunction(() => document.querySelector('#list-summary')?.textContent === '5,000 个构件');
  await page.locator('.tree-toggle').first().click();
  assert.equal(await page.locator('.component-row').count(), 0, 'collapsing a category skips all of its descendants');
  await page.locator('.tree-toggle').first().click();
  assert.ok(await page.locator('.component-row').count() > 0);
  return { virtualTree: true, components: 5000, renderedRows: await page.locator('.component-row').count() };
}
