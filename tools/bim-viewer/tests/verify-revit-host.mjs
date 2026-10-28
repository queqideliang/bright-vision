import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, realpath, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { MODEL_LIMITS, parseModelZip } from '../src/import/parse.ts';
import { ModelArchive } from '../src/import/zip.ts';
import { createQuantityIndex, QUANTITY_KEYS, QUANTITY_DEFINITIONS, quantityCsv } from '../src/quantities.ts';

const MAX_REPORT_BYTES = 16 * 1024 * 1024;
const MAX_SMALL_ZIP_BYTES = 256 * 1024 * 1024;
const powers = { length: 1, area: 2, volume: 3 };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (left, right) => process.platform === 'win32'
  ? path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase() : path.resolve(left) === path.resolve(right);
const sourceKey = source => JSON.stringify([String(source.elementId), source.uniqueId]);

async function jsonFile(file, maxBytes = MAX_REPORT_BYTES) {
  const info = await stat(file);
  assert.ok(info.isFile() && info.size <= maxBytes, `JSON file is not a bounded regular file: ${file}`);
  const bytes = await readFile(file);
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\ufeff/u, '');
  return { data: JSON.parse(text), sha256: digest(bytes) };
}

async function fileIdentity(file) {
  assert.ok(typeof file === 'string' && path.isAbsolute(file), 'Host file paths must be absolute');
  const info = await stat(file);
  assert.ok(info.isFile(), `Not a regular file: ${file}`);
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return { path: await realpath(file), bytes: info.size, sha256: hash.digest('hex') };
}

async function preparationBaseline(directory, modelPath) {
  // The preparation record is an independent file made before the host run.
  // Only accept one describing this exact copied RVT, rather than an unrelated ancestor record.
  for (let current = directory, depth = 0; depth < 5; depth++) {
    try {
      const file = path.join(current, 'prepared-model.json');
      const { data } = await jsonFile(file);
      if (typeof data.copy === 'string' && samePath(data.copy, modelPath)) return { path: file, data };
    } catch (error) { if (error?.code !== 'ENOENT') throw error; }
    const parent = path.dirname(current); if (parent === current) break; current = parent;
  }
  return null;
}

function near(actual, expected, where) {
  const tolerance = Math.max(1e-10, Math.abs(expected) * 1e-10);
  assert.ok(typeof actual === 'number' && Number.isFinite(actual) && Math.abs(actual - expected) <= tolerance,
    `${where}: exported ${actual}, independently converted ${expected}, tolerance ${tolerance}`);
}

function validateOptions(reported, packaged) {
  assert.ok(object(reported) && object(packaged), 'Export options must be recorded by host and ZIP');
  const aliases = {
    IncludeTextures: 'textures', IncludeSystemTree: 'systems', IncludeSpaceTree: 'spaces',
    IncludeDisciplineTree: 'disciplines', IncludeCadLinks: 'visibleCadGeometry', IncludeGrids: 'gridCurves', Include2DViews: 'selected2DDwg',
  };
  for (const [field, key] of Object.entries(aliases)) {
    const value = reported[field] ?? reported[field[0].toLowerCase() + field.slice(1)] ?? reported[key];
    assert.equal(typeof value, 'boolean', `Missing host export option ${field}`);
    assert.equal(value, packaged[key], `Host/ZIP option differs: ${field}`);
  }
}

async function validate(reportPath, outputDirectory) {
  const { data: report, sha256: reportSha256 } = await jsonFile(reportPath);
  assert.ok(object(report), 'Host report must be an object');
  assert.ok(['pass', 'passed', 'success', 'succeeded', 'exported', 'completed'].includes(String(report.result).toLowerCase()), 'Host did not report successful export');
  assert.ok(typeof report.runId === 'string' && report.runId.length > 0, 'Missing run ID');
  assert.ok(object(report.host) && typeof report.host.version === 'string' && typeof report.host.build === 'string', 'Missing Revit host identity');
  assert.ok(object(report.model) && object(report.export) && object(report.view), 'Incomplete host report');
  assert.equal(report.model.sourceSaved, false, 'Host must not save the source RVT');
  assert.equal(typeof report.model.isModifiedBefore, 'boolean');
  assert.equal(typeof report.model.isModifiedAfter, 'boolean');
  const modelFile = await fileIdentity(report.model.path);
  assert.equal(modelFile.sha256, String(report.model.sha256).toLowerCase(), 'Source RVT hash differs from host pre-open hash');
  if (report.model.sha256After !== undefined) assert.equal(modelFile.sha256, String(report.model.sha256After).toLowerCase(), 'Source RVT hash differs from host after-export hash');
  if (report.model.hashUnchanged !== undefined) assert.equal(report.model.hashUnchanged, true);
  const baseline = await preparationBaseline(outputDirectory, modelFile.path);
  if (baseline) assert.equal(modelFile.sha256, String(baseline.data.sha256).toLowerCase(), 'Source RVT differs from independent preparation baseline');

  let plugin = null;
  if (report.plugin) {
    plugin = await fileIdentity(report.plugin.path);
    assert.equal(plugin.sha256, String(report.plugin.sha256).toLowerCase(), 'Installed exporter differs from recorded plugin hash');
    assert.ok(/^1\.4\.0(?:\.0)?$/u.test(String(report.plugin.assemblyVersion)), 'Unexpected exporter assembly version');
    const buildPath = fileURLToPath(new URL('../../bright-revit-exporter/artifacts/BV-1.4.0-final/build-manifest.json', import.meta.url));
    const { data: build } = await jsonFile(buildPath);
    assert.equal(build.packageVersion, '1.4.0');
    const variants = build.variants.filter(variant => String(variant.year) === report.host.version
      && variant.files.some(file => file.name === 'Bright.Vision.Revit.dll' && file.sha256.toLowerCase() === plugin.sha256));
    assert.ok(variants.length > 0, 'Installed plugin is not a frozen 1.4.0 payload for this Revit year');
    plugin.verifiedFrozenVariants = variants.map(variant => variant.id);
    if (baseline?.data.plugin) assert.equal(plugin.sha256, baseline.data.plugin.sha256.toLowerCase(), 'Plugin differs from pre-host preparation baseline');
  }

  const zipFile = await fileIdentity(report.export.path);
  assert.ok(zipFile.bytes > 0 && zipFile.bytes <= MAX_SMALL_ZIP_BYTES, 'Small-model host validation caps ZIP input at 256 MiB');
  assert.equal(zipFile.bytes, report.export.zipBytes, 'Actual ZIP size differs from host report');
  if (report.export.sha256 !== undefined) assert.equal(zipFile.sha256, String(report.export.sha256).toLowerCase(), 'Actual ZIP hash differs from host report');
  assert.ok(typeof report.export.elapsedSeconds === 'number' && Number.isFinite(report.export.elapsedSeconds) && report.export.elapsedSeconds >= 0);
  const bytes = await readFile(zipFile.path);
  assert.equal(digest(bytes), zipFile.sha256, 'ZIP changed between identity check and parsing');
  const archive = new ModelArchive(bytes, MODEL_LIMITS);
  const entries = await archive.inspect();
  const manifestEntry = entries.find(entry => entry.name === 'manifest.json');
  assert.ok(manifestEntry && manifestEntry.originalSize <= 1024 * 1024, 'Missing or oversized root manifest.json');
  const manifestBytes = new Uint8Array(manifestEntry.originalSize); let offset = 0;
  for await (const chunk of archive.chunks(manifestEntry)) { manifestBytes.set(chunk, offset); offset += chunk.length; }
  const manifest = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifestBytes));
  assert.equal(manifest.exporterVersion, '1.4.0'); assert.equal(manifest.independentExporter, true);
  assert.equal(manifest.exportMethod, 'CustomExporter/IExportContext'); assert.equal(manifest.lengthUnit, 'm');
  assert.equal(manifest.revitVersion, report.host.version); assert.equal(manifest.revitBuild, report.host.build);
  assert.equal(manifest.viewUniqueId, report.view.uniqueId); assert.equal(manifest.viewName, report.view.name);
  assert.ok(typeof manifest.documentScope === 'string' && manifest.documentScope.length > 0, 'Missing manifest root document scope');
  if (report.manifestDocumentScope !== undefined) assert.equal(manifest.documentScope, report.manifestDocumentScope);
  assert.equal(manifest.quantitySnapshot?.schemaVersion, 1); assert.equal(manifest.quantitySnapshot?.source, 'revit-parameters');
  validateOptions(report.export.options, manifest.exportOptions);

  const model = await parseModelZip(bytes, path.basename(zipFile.path));
  assert.ok(model.components.length > 0 && Object.keys(model.geometries).length > 0 && Object.keys(model.nodes).length > 0 && model.stats.triangles > 0,
    'The real ZIP must contain nonzero components, nodes and mesh triangles');
  assert.equal(model.components.length, report.export.componentCount); assert.equal(model.components.length, manifest.components);
  assert.equal(Object.keys(model.nodes).length, report.export.nodeCount); assert.equal(Object.keys(model.nodes).length, manifest.nodes);
  assert.equal(Object.keys(model.geometries).length, report.export.geometryCount); assert.equal(Object.keys(model.geometries).length, manifest.uniqueGeometries);
  // Parser.stats.triangles counts unique geometries; PackageWriter counts every written node placement.
  const placedTrianglesByNodes = Object.values(model.nodes).reduce((sum, node) => sum + model.geometries[node.geometryId].indices.length / 3, 0);
  const placedTrianglesByComponents = model.components.reduce((sum, component) => sum + component.nodeIds.reduce((subtotal, id) => subtotal + model.geometries[model.nodes[id].geometryId].indices.length / 3, 0), 0);
  assert.ok(placedTrianglesByNodes > 0); assert.equal(placedTrianglesByNodes, manifest.placedTriangles);
  assert.equal(placedTrianglesByNodes, report.export.triangleCount, 'Placed triangle count differs from actual ZIP node placements');
  const modelJsonBytes = entries.filter(entry => entry.name.endsWith('.json') && entry.name !== 'manifest.json').reduce((sum, entry) => sum + entry.originalSize, 0);
  const assetBytes = entries.filter(entry => entry.name.startsWith('texture/') || entry.name.startsWith('drawings/')).reduce((sum, entry) => sum + entry.originalSize, 0);
  assert.equal(modelJsonBytes, report.export.modelJsonBytes); assert.equal(assetBytes, report.export.assetBytes);
  assert.equal(modelJsonBytes, manifest.exportStatistics.modelJsonBytes); assert.equal(assetBytes, manifest.exportStatistics.assetBytes);
  assert.equal(modelJsonBytes + assetBytes, manifest.exportStatistics.exportDataBytes);
  assert.ok(model.components.every(component => component.quantitySnapshot && !component.quantityIssue), 'A component has a malformed or missing 1.4 quantity snapshot');

  assert.ok(Array.isArray(report.referenceQuantities), 'Missing independent native-API reference rows');
  const references = new Map();
  for (const reference of report.referenceQuantities) {
    assert.ok(object(reference) && typeof reference.elementId === 'string' && typeof reference.uniqueId === 'string' && object(reference.metrics), 'Invalid native reference identity');
    if (reference.documentScope !== undefined) assert.equal(reference.documentScope, manifest.documentScope);
    if (reference.linkInstanceScope !== undefined) assert.equal(reference.linkInstanceScope, 'root');
    const key = sourceKey(reference);
    assert.ok(!references.has(key), 'Duplicate native reference identity'); references.set(key, reference);
  }
  const rootSources = new Map(); const linkedSources = new Set(); const matched = new Set(); const comparisons = new Map();
  let matchedRootRecords = 0;
  for (const component of model.components) {
    const snapshot = component.quantitySnapshot; const source = snapshot.sourceElement;
    const fullKey = JSON.stringify(source);
    if (source.documentScope !== manifest.documentScope || source.linkInstanceScope !== 'root') { linkedSources.add(fullKey); continue; }
    const key = sourceKey(source); rootSources.set(key, { source, category: component.category, categoryCode: component.categoryCode });
    const reference = references.get(key); if (!reference) continue;
    matched.add(key); matchedRootRecords++;
    for (const metric of QUANTITY_KEYS) {
      const native = reference.metrics[metric]; const exported = snapshot.metrics[metric]; const definition = QUANTITY_DEFINITIONS[metric];
      assert.ok(object(native) && ['available', 'missing', 'invalid', 'unavailable'].includes(native.status), `Invalid native metric ${key}/${metric}`);
      assert.equal(native.unit, definition.unit); assert.equal(native.parameter, definition.parameter);
      assert.equal(exported.unit, definition.unit); assert.equal(exported.parameter, definition.parameter);
      assert.equal(exported.status, native.status, `Native/exported status mismatch for ${key}/${metric}`);
      let independentlyConverted = null;
      if (native.status === 'available') {
        assert.ok(typeof native.rawValue === 'number' && Number.isFinite(native.rawValue) && native.rawValue >= 0, `Invalid independent raw native value ${key}/${metric}`);
        // Deliberately do not use reference.value or the exporter's conversion helper as an oracle.
        independentlyConverted = native.rawValue * Math.pow(0.3048, powers[metric]);
        near(exported.value, independentlyConverted, `${source.elementId}/${metric}`);
      } else assert.equal(exported.value, null);
      comparisons.set(`${key}/${metric}`, { elementId: source.elementId, uniqueId: source.uniqueId, category: reference.category,
        categoryCode: reference.categoryCode ?? '', metric, status: native.status, rawValue: native.rawValue ?? null,
        independentlyConverted, exportedValue: exported.value, unit: definition.unit, parameter: definition.parameter });
    }
  }
  const details = [...comparisons.values()];
  const availableComparisons = details.filter(comparison => comparison.status === 'available');
  assert.ok(matched.size > 0 && availableComparisons.length > 0, 'No root-document quantity with an independently recorded available raw native value was matched');
  const categoryCodes = new Set([...report.referenceQuantities.map(row => row.categoryCode ?? `name:${row.category}`), ...[...rootSources.values()].map(row => row.categoryCode || `name:${row.category}`)]);
  const categoryCoverage = [...categoryCodes].map(categoryCode => {
    const refs = [...references].filter(([, row]) => (row.categoryCode ?? `name:${row.category}`) === categoryCode);
    const exported = [...rootSources].filter(([, row]) => (row.categoryCode || `name:${row.category}`) === categoryCode);
    const matchedReferences = refs.filter(([key]) => matched.has(key));
    const refKeys = new Set(refs.map(([key]) => key));
    const nativeAvailableComparisons = availableComparisons.filter(row => refKeys.has(sourceKey(row))).length;
    return { categoryCode, category: refs[0]?.[1].category ?? exported[0]?.[1].category ?? '', nativeReferenceElements: refs.length,
      exportedRootSources: exported.length, matchedSources: matchedReferences.length, nativeAvailableComparisons,
      covered: nativeAvailableComparisons > 0, reason: nativeAvailableComparisons > 0 ? 'matched-native-values' : 'not-covered-by-an-available-matched-native-value' };
  });
  const targetCoverage = Array.isArray(report.referenceCategoryTargets) ? report.referenceCategoryTargets.map(target => {
    const coverage = categoryCoverage.find(row => row.categoryCode === target.categoryCode);
    return { ...target, ...(coverage ?? { nativeReferenceElements: 0, exportedRootSources: 0, matchedSources: 0, nativeAvailableComparisons: 0, covered: false, reason: 'no-reference-or-exported-match' }) };
  }) : [];
  const index = createQuantityIndex(model.components); const summary = index.summarize(model.components);
  const csvPath = path.join(outputDirectory, 'host-quantities.csv');
  await writeFile(csvPath, quantityCsv(index.buildRows(model.components, 'component'), {
    language: 'zh', modelName: model.name, scope: '全部实际导出构件；原生参数独立核验仅覆盖已匹配的根文档来源', mode: 'component',
  }), 'utf8');
  return {
    result: 'PASS', validationKind: 'real-zip-and-recorded-native-quantity-data', runId: report.runId,
    hostReport: { path: reportPath, sha256: reportSha256 }, host: report.host,
    source: { ...modelFile, preparationBaseline: baseline?.path ?? null, preparationBaselineMatched: !!baseline,
      sourceSaved: false, isModifiedBefore: report.model.isModifiedBefore, isModifiedAfter: report.model.isModifiedAfter },
    plugin, zip: zipFile, view: report.view, elapsedSeconds: report.export.elapsedSeconds,
    counts: { components: model.components.length, nodes: Object.keys(model.nodes).length, uniqueGeometries: Object.keys(model.geometries).length,
      uniqueGeometryTriangles: model.stats.triangles, nodePlacementTriangles: placedTrianglesByNodes, componentReferencedTriangles: placedTrianglesByComponents,
      modelJsonBytes, assetBytes, zipBytes: zipFile.bytes },
    quantities: { summary, matchedRootSources: matched.size, matchedRootRecords, nativeAvailableComparisons: availableComparisons.length,
      unmatchedRootSources: [...rootSources.keys()].filter(key => !matched.has(key)).length,
      unmatchedNativeReferences: [...references.keys()].filter(key => !matched.has(key)).length,
      unmatchedRootSourceExamples: [...rootSources].filter(([key]) => !matched.has(key)).slice(0, 20).map(([, row]) => row),
      unmatchedNativeReferenceExamples: [...references].filter(([key]) => !matched.has(key)).slice(0, 20).map(([, row]) => ({ elementId: row.elementId, uniqueId: row.uniqueId, category: row.category, categoryCode: row.categoryCode ?? '' })),
      linkedOrOtherScopeSourcesNotIndependentlyChecked: linkedSources.size, categoryCoverage, targetCoverage, comparisons: details },
    warnings: { importer: model.warnings, exporter: manifest.warnings ?? [], nativeReferenceCollection: report.nativeReferenceCollection ?? null },
    outputs: { quantitiesCsv: csvPath },
    limitations: ['This validator reads the ZIP and host-written API reference data; it does not observe live Ribbon clicks or dialogs.',
      'The independent oracle is native rawValue converted with 0.3048 to the metric power; report.value is not used.',
      'Linked or unmatched sources and categories without available matched native data are not independently verified.',
      'Source file hashes verify saved bytes; in-memory Revit modifications are recorded separately.'],
    liveRibbonClickVerified: false,
  };
}

export async function verifyRevitHost(hostReportPath) {
  const reportPath = path.resolve(hostReportPath); const outputDirectory = path.dirname(reportPath);
  const outputPath = path.join(outputDirectory, 'host-data-validation.json');
  try {
    const result = await validate(reportPath, outputDirectory);
    await writeFile(outputPath, JSON.stringify(result, null, 2), 'utf8'); return result;
  } catch (error) {
    await writeFile(outputPath, JSON.stringify({ result: 'FAIL', hostReportPath: reportPath, error: String(error?.stack ?? error),
      outputs: { quantitiesCsv: null }, liveRibbonClickVerified: false }, null, 2), 'utf8');
    throw error;
  }
}

if (process.argv[1] && samePath(process.argv[1], fileURLToPath(import.meta.url))) {
  if (!process.argv[2]) { console.error('Usage: node --experimental-strip-types tests/verify-revit-host.mjs <host-report.json>'); process.exitCode = 2; }
  else {
    try {
      const result = await verifyRevitHost(process.argv[2]);
      console.log(JSON.stringify({ result: result.result, runId: result.runId, counts: result.counts,
        matchedRootSources: result.quantities.matchedRootSources, nativeAvailableComparisons: result.quantities.nativeAvailableComparisons,
        targetCoverage: result.quantities.targetCoverage, output: path.join(path.dirname(path.resolve(process.argv[2])), 'host-data-validation.json'),
        liveRibbonClickVerified: false }));
    } catch (error) { console.error(String(error?.message ?? error)); process.exitCode = 1; }
  }
}
