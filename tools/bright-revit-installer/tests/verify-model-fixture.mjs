import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseModelZip } from '../../bim-viewer/src/import/parse.ts';

const file = new URL('../../bright-revit-exporter/artifacts/export-fixture.zip', import.meta.url);
const model = await parseModelZip(new Uint8Array(readFileSync(file)), 'Bright.Vision export fixture');
assert.equal(model.units?.length, 'm');
assert.equal(model.components.length, 2);
assert.equal(Object.keys(model.geometries).length, 1);
assert.equal(model.levels[0]?.name, '一层');
assert.equal(model.levels[0]?.elevation, 3);
assert.equal(model.levels[0]?.componentIds.length, 2);
const a = model.nodes[model.components[0].nodeIds[0]];
const b = model.nodes[model.components[1].nodeIds[0]];
assert.deepEqual(Array.from(a.matrix).slice(12, 15), [10, 20, 3]);
assert.deepEqual(Array.from(b.matrix).slice(12, 15), [15, 20, 3]);
assert.equal(b.matrix[0], -1);
assert.equal(a.geometryId, b.geometryId);
assert.equal(a.materialId, 'fixture-blue');
assert.equal(model.materials[a.materialId].raw.color, '#1688ff');
assert.equal(model.components[0].properties['来源标识'].UniqueId, 'fixture-unique-1');
assert.equal(model.components[0].properties['参数']['数值'], 42.5);
assert.equal(model.components[0].properties['参数']['说明'], '中文、引号 " 与换行\n正确转义');
assert(model.warnings.some(warning => warning.includes('未包含材质贴图')));
const levelModel = await parseModelZip(new Uint8Array(readFileSync(new URL('../../bright-revit-exporter/artifacts/export-level-fixture.zip', import.meta.url))), 'Level semantics fixture');
assert.equal(levelModel.levels.find(level => level.name === 'Multi-placement')?.elevation, undefined);
assert.equal(levelModel.levels.find(level => level.name === 'Tilted')?.elevation, undefined);
assert.equal(levelModel.levels.find(level => level.name === 'Stable')?.elevation, 3);
console.log(JSON.stringify({ result: 'PASS', components: model.components.length, sharedGeometries: model.stats.geometries,
  lengthUnit: model.units.length, levels: model.levels.map(level => level.name), mirroredTransform: true,
  metadataEscaping: true, materialAndPropertyReferences: true, levelSemantics: true }));
