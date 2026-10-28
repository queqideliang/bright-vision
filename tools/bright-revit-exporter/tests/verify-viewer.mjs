import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { parseModelZip } from '../../bim-viewer/src/import/parse.ts';

const fixtureRoot = resolve(process.argv[2] ?? 'tools/bright-revit-exporter/artifacts/independent-1.3-check');
for (const name of ['export-fixture.zip', 'export-assets-fixture.zip']) {
  const bytes = new Uint8Array(await readFile(resolve(fixtureRoot, name)));
  const model = await parseModelZip(bytes, name);
  assert.ok(model.components.length > 0);
  assert.ok(Object.keys(model.geometries).length > 0);
  if (name.includes('assets')) {
    assert.ok(model.textures['fixture.png']);
    assert.equal(model.materials['texture-fixture'].raw.map, 'texture/fixture.png');
    assert.ok(!model.warnings.some((item) => item.includes('未包含材质贴图')));
  }
  console.log(JSON.stringify({ name, components: model.components.length, geometries: Object.keys(model.geometries).length, textures: Object.keys(model.textures).length, warnings: model.warnings }));
}
