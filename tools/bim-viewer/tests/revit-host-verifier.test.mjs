import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { strToU8, zipSync } from 'fflate';
import { verifyRevitHost } from './verify-revit-host.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const definitions = {
  length: { unit: 'm', parameter: 'CURVE_ELEM_LENGTH', power: 1 },
  area: { unit: 'm2', parameter: 'HOST_AREA_COMPUTED', power: 2 },
  volume: { unit: 'm3', parameter: 'HOST_VOLUME_COMPUTED', power: 3 },
};

/** Synthetic inputs test the verifier itself. They are not real Revit export evidence. */
async function syntheticFixture(t, hooks = {}) {
  const root = await mkdtemp(path.join(tmpdir(), 'bv-host-validation-synthetic-'));
  const canonical = await realpath(root);
  t.after(async () => {
    assert.equal(await realpath(root), canonical);
    assert.ok(path.basename(canonical).startsWith('bv-host-validation-synthetic-'));
    assert.ok(samePath(path.dirname(canonical), await realpath(tmpdir())));
    await rm(canonical, { recursive: true, force: true });
  });
  const modelPath = path.join(root, 'synthetic.rvt');
  const sourceBytes = Buffer.from('synthetic model bytes; not a Revit file');
  await writeFile(modelPath, sourceBytes);
  const references = [1, 2].map(elementId => ({
    elementId: String(elementId), uniqueId: `unique-${elementId}`, documentScope: 'root-doc', linkInstanceScope: 'root',
    categoryCode: elementId === 1 ? 'walls-code' : 'floors-code', category: elementId === 1 ? 'Walls' : 'Floors',
    metrics: Object.fromEntries(Object.entries(definitions).map(([key, definition]) => [key, {
      status: 'available', rawValue: elementId * 10 ** definition.power,
      value: -999999, // Deliberately untrustworthy declared SI value: validator must ignore it.
      unit: definition.unit, parameter: definition.parameter,
    }])),
  }));
  const components = references.map(reference => ({
    Id: `scene-${reference.elementId}`, name: reference.category, type: reference.category, BuildInCategory: reference.categoryCode,
    Nodes: [`n${reference.elementId}`], QuantitySnapshot: {
      schemaVersion: 1, source: 'revit-parameters',
      sourceElement: { uniqueId: reference.uniqueId, elementId: reference.elementId, documentScope: 'root-doc', linkInstanceScope: hooks.linkSecond && reference.elementId === '2' ? 'linked-placement' : 'root' },
      metrics: Object.fromEntries(Object.entries(definitions).map(([key, definition]) => [key, {
        status: 'available', value: reference.metrics[key].rawValue * Math.pow(0.3048, definition.power), unit: definition.unit, parameter: definition.parameter,
      }])),
    },
  }));
  if (hooks.alterSnapshots) hooks.alterSnapshots(components.map(component => component.QuantitySnapshot));
  const files = {
    'components/components-0.json': components,
    'nodes/nodes-0.json': { n1: { geometry: 'g', material: 'm' }, n2: { geometry: 'g', material: 'm' } },
    'geometries/geometries-0.json': { g: { type: 'MESH', positions: [0, 0, 0, 1, 0, 0, 0, 1, 0], indices: [0, 1, 2] } },
    'materials/materials-0.json': { m: { color: [1, 0, 0] } },
    'LevelInfo.json': { '1F': { elements: { components: [{ id: 'scene-1' }, { id: 'scene-2' }] } } },
  };
  const encoded = Object.fromEntries(Object.entries(files).map(([name, value]) => [name, strToU8(JSON.stringify(value))]));
  const modelJsonBytes = Object.values(encoded).reduce((sum, bytes) => sum + bytes.byteLength, 0);
  const options = { geometry: true, properties: true, textures: true, systems: true, spaces: true, disciplines: true, visibleCadGeometry: true, gridCurves: false, selected2DDwg: false };
  const manifest = {
    exporterVersion: '1.4.0', independentExporter: true, exportMethod: 'CustomExporter/IExportContext', lengthUnit: 'm',
    revitVersion: '2020', revitBuild: 'synthetic-build', viewUniqueId: 'view-id', viewName: '{3D}', documentScope: 'root-doc',
    quantitySnapshot: { schemaVersion: 1, source: 'revit-parameters' }, exportOptions: options,
    components: 2, nodes: 2, uniqueGeometries: 1, placedTriangles: 2,
    exportStatistics: { modelJsonBytes, assetBytes: 0, exportDataBytes: modelJsonBytes }, warnings: [],
  };
  if (hooks.alterManifest) hooks.alterManifest(manifest);
  const zip = zipSync({ ...encoded, 'manifest.json': strToU8(JSON.stringify(manifest)) });
  const zipPath = path.join(root, 'synthetic.zip'); await writeFile(zipPath, zip);
  const report = {
    result: 'PASS', runId: 'synthetic-verifier-unit-test', host: { version: '2020', build: 'synthetic-build' },
    model: { path: modelPath, sha256: hash(sourceBytes), sha256After: hash(sourceBytes), sourceSaved: false, isModifiedBefore: false, isModifiedAfter: false, hashUnchanged: true },
    view: { id: '3', uniqueId: 'view-id', name: '{3D}' }, manifestDocumentScope: 'root-doc',
    export: { path: zipPath, sha256: hash(zip), zipBytes: zip.byteLength, elapsedSeconds: 1.2,
      componentCount: 2, nodeCount: 2, geometryCount: 1, triangleCount: 2, modelJsonBytes, assetBytes: 0,
      options: { includeTextures: true, includeSystemTree: true, includeSpaceTree: true, includeDisciplineTree: true, includeCadLinks: true, includeGrids: false, include2DViews: false } },
    referenceQuantities: references,
    referenceCategoryTargets: [{ kind: 'wall', categoryCode: 'walls-code' }, { kind: 'floor', categoryCode: 'floors-code' }, { kind: 'column', categoryCode: 'missing-code' }],
  };
  if (hooks.alterReport) hooks.alterReport(report);
  const reportPath = path.join(root, 'host-report.json'); await writeFile(reportPath, JSON.stringify(report), 'utf8');
  return { root, reportPath, modelPath, report, output: path.join(root, 'host-data-validation.json') };
}

function samePath(left, right) { return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right; }

test('synthetic validator test distinguishes placed triangles and ignores report.value as an oracle', async t => {
  const fixture = await syntheticFixture(t);
  const result = await verifyRevitHost(fixture.reportPath);
  assert.equal(result.result, 'PASS'); assert.equal(result.counts.uniqueGeometryTriangles, 1);
  assert.equal(result.counts.nodePlacementTriangles, 2); assert.equal(result.quantities.nativeAvailableComparisons, 6);
  assert.equal(result.quantities.targetCoverage.find(target => target.kind === 'column').covered, false);
  assert.equal(result.liveRibbonClickVerified, false);
  assert.ok((await readFile(path.join(fixture.root, 'host-quantities.csv'), 'utf8')).startsWith('\ufeff'));
});

test('synthetic validator rejects mismatched independently recorded raw values', async t => {
  const fixture = await syntheticFixture(t, { alterReport: report => { report.referenceQuantities[0].metrics.length.rawValue = 11; } });
  await assert.rejects(verifyRevitHost(fixture.reportPath), /independently converted/u);
  assert.equal(JSON.parse(await readFile(fixture.output, 'utf8')).result, 'FAIL');
});

test('synthetic validator rejects changed source files and ZIP identities', async t => {
  const sourceFixture = await syntheticFixture(t);
  await writeFile(sourceFixture.modelPath, 'modified bytes');
  await assert.rejects(verifyRevitHost(sourceFixture.reportPath), /Source RVT hash/u);
  const zipFixture = await syntheticFixture(t, { alterReport: report => { report.export.sha256 = '0'.repeat(64); } });
  await assert.rejects(verifyRevitHost(zipFixture.reportPath), /Actual ZIP hash/u);
});

test('synthetic validator rejects wrong exporter versions, malformed metric units and placed count mismatches', async t => {
  const version = await syntheticFixture(t, { alterManifest: manifest => { manifest.exporterVersion = '1.3.1'; } });
  await assert.rejects(verifyRevitHost(version.reportPath), /1\.3\.1/u);
  const units = await syntheticFixture(t, { alterSnapshots: snapshots => { snapshots[0].metrics.area.unit = 'ft2'; } });
  await assert.rejects(verifyRevitHost(units.reportPath), /malformed or missing/u);
  const triangles = await syntheticFixture(t, { alterReport: report => { report.export.triangleCount = 99; } });
  await assert.rejects(verifyRevitHost(triangles.reportPath), /Placed triangle count/u);
});

test('synthetic validator labels linked and unmatched categories uncovered', async t => {
  const fixture = await syntheticFixture(t, { linkSecond: true });
  const result = await verifyRevitHost(fixture.reportPath);
  assert.equal(result.quantities.matchedRootSources, 1);
  assert.equal(result.quantities.linkedOrOtherScopeSourcesNotIndependentlyChecked, 1);
  assert.equal(result.quantities.targetCoverage.find(target => target.kind === 'floor').covered, false);
  assert.equal(result.quantities.unmatchedNativeReferences, 1);
});

test('synthetic validator needs at least one available matched native quantity', async t => {
  const fixture = await syntheticFixture(t, {
    alterSnapshots: snapshots => { for (const snapshot of snapshots) for (const metric of Object.values(snapshot.metrics)) { metric.status = 'missing'; metric.value = null; } },
    alterReport: report => { for (const reference of report.referenceQuantities) for (const metric of Object.values(reference.metrics)) { metric.status = 'missing'; metric.rawValue = null; metric.value = null; } },
  });
  await assert.rejects(verifyRevitHost(fixture.reportPath), /No root-document quantity/u);
});
