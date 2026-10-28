import test from 'node:test';
import assert from 'node:assert/strict';
import { Box3, BufferAttribute, BufferGeometry, Group, Matrix4, Mesh, MeshPhongMaterial, PerspectiveCamera, Ray, Vector3 } from 'three';
import { ModelViewer } from '../src/viewer/model-viewer.ts';
import { BoundsTree, TriangleIndex } from '../src/viewer/spatial.ts';

/** Exercise state/coordinate rules without allocating a browser or GPU context. */
function viewerFixture(): ModelViewer {
  const viewer = Object.create(ModelViewer.prototype) as ModelViewer;
  const boxes = [new Box3(new Vector3(0, 0, 0), new Vector3(2, 2, 2)), new Box3(new Vector3(4, 0, 0), new Vector3(6, 2, 2)), new Box3(new Vector3(8, 0, 0), new Vector3(10, 2, 2))];
  Object.assign(viewer, {
    componentIds: ['a', 'b', 'c'], componentIndex: new Map([['a', 0], ['b', 1], ['c', 2]]), componentBounds: boxes,
    componentHasGeometry: [true, true, true], stateBytes: new Uint8Array([255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255]),
    selectedIds: new Set<string>(), selectedId: null, selectionBox: { visible: false, box: new Box3() },
    sectionHelper: { visible: false, box: new Box3() }, center: new Vector3(1000, 2000, 3000),
    bounds: new Box3(new Vector3(0, 0, 0), new Vector3(10, 2, 2)), materials: new Set(), batches: [],
    stateTexture: null, ghostMode: false, isolatedIds: null, sectionBounds: null, sectionPlanes: [],
    onSelect: () => {}, requestRender: () => {}, camera: new PerspectiveCamera(45, 1, 0.001, 1000),
    controls: { target: new Vector3(), update() {}, minDistance: 0.01, maxDistance: 10000 },
    lastPick: { batches: 0, triangles: 0, milliseconds: 0 }, instanceCount: 0,
    measurementPoints: [], measurementRoot: new Group(), renderer: { domElement: { style: {} } },
  });
  return viewer;
}

test('multi-selection preserves shared state and toggle removes only the selected item', () => {
  const viewer = viewerFixture();
  const events: string[][] = [];
  viewer.onSelectionChange = ids => events.push(ids);
  viewer.selectMany(['a', 'b']);
  assert.deepEqual(viewer.getSelectedIds(), ['a', 'b']);
  assert.equal(viewer.getSelectedId(), 'b');
  viewer.selectMany(['a'], 'toggle');
  assert.deepEqual(viewer.getSelectedIds(), ['b']);
  viewer.setVisible(['b'], false);
  assert.deepEqual(viewer.getSelectedIds(), []);
  assert.equal(viewer.isComponentVisible('b'), false);
  assert.deepEqual(events, [['a', 'b'], ['b'], []]);
});

test('ghost isolation keeps the focus set normal and can return to solid isolation/show all', () => {
  const viewer = viewerFixture();
  viewer.select('a');
  viewer.isolate(['a']);
  viewer.setGhostMode(true);
  assert.deepEqual(viewer.captureView().ghostedIds, ['b', 'c']);
  assert.deepEqual(viewer.getVisibleIds(), ['a']);
  viewer.select('b'); // Tree selection may promote a ghost into a visible selection.
  assert.equal(viewer.isComponentVisible('b'), true);
  viewer.setGhostMode(false);
  assert.deepEqual(viewer.captureView().ghostedIds, []);
  viewer.showAll();
  assert.deepEqual(viewer.getVisibleIds(), ['a', 'b', 'c']);
});

test('section bounds and viewpoint cameras round-trip original world coordinates', () => {
  const viewer = viewerFixture();
  const clip = { min: [1000, 2000, 3000] as [number, number, number], max: [1006, 2002, 3002] as [number, number, number] };
  viewer.setSectionBox(clip);
  viewer.selectMany(['a', 'b']);
  viewer.setGhostMode(true);
  viewer.isolate(['a', 'b']);
  const saved = viewer.captureView();
  saved.position = [1010, 1980, 3010]; saved.target = [1002, 2001, 3001];
  viewer.showAll(); viewer.setSectionBox(null); viewer.select(null);
  viewer.restoreView(saved);
  const restored = viewer.captureView();
  assert.deepEqual(restored, saved);
  assert.deepEqual(viewer.getComponentBounds('a'), { min: [1000, 2000, 3000], max: [1002, 2002, 3002] });
  assert.throws(() => viewer.setSectionBox({ min: [2, 0, 0], max: [1, 1, 1] }));
  assert.throws(() => viewer.restoreView({ ...saved, position: [NaN, 0, 0] }));
  assert.throws(() => viewer.restoreView({ ...saved, position: [1e300, 0, 0] }));
});

test('two-point measurement reports raw world points and the unscaled model distance', () => {
  const viewer = viewerFixture();
  viewer.setMeasurementMode(true);
  const add = (point: Vector3) => (viewer as unknown as { addMeasurement(point: Vector3): void }).addMeasurement(point);
  add(new Vector3(0, 0, 0));
  assert.deepEqual(viewer.getMeasurement(), { points: [[1000, 2000, 3000]], distance: null });
  add(new Vector3(3, 4, 0));
  assert.deepEqual(viewer.getMeasurement(), { points: [[1000, 2000, 3000], [1003, 2004, 3000]], distance: 5 });
  add(new Vector3(1, 1, 1));
  assert.deepEqual(viewer.getMeasurement(), { points: [[1001, 2001, 3001]], distance: null });
  viewer.clearMeasurement();
  assert.equal(viewer.getMeasurement(), null);
});

test('instance picking transforms rays correctly and rejects hidden or sectioned surfaces', () => {
  const viewer = viewerFixture();
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new BufferAttribute(new Float32Array([0, 0, 0, 2, 0, 0, 0, 2, 0]), 3));
  geometry.setIndex(new BufferAttribute(new Uint32Array([0, 1, 2]), 1));
  const matrices = [new Matrix4().makeScale(2, 3, 1).setPosition(0, 0, 2), new Matrix4().makeScale(1, 1, 2).setPosition(0, 0, 4)];
  const boxes = matrices.map(matrix => new Box3(new Vector3(0, 0, 0), new Vector3(2, 2, 0)).applyMatrix4(matrix));
  const box = boxes.reduce((result, item) => result.union(item), new Box3());
  const batch = { mesh: new Mesh(geometry, new MeshPhongMaterial()), bounds: box, owners: new Uint32Array([0, 1]), componentIndices: [0, 1],
    triangleIndex: new TriangleIndex(geometry), instanceMatrices: matrices, instanceBounds: boxes, instanceTree: BoundsTree.fromBoxes(boxes) };
  Object.assign(viewer, { batches: [batch], batchTree: BoundsTree.fromBoxes([box]) });
  const pick = (ray: Ray) => (viewer as unknown as { pickRay(ray: Ray): { id: string; point: Vector3 } | null }).pickRay(ray);
  const ray = new Ray(new Vector3(0.2, 0.2, 10), new Vector3(0, 0, -1));
  assert.equal(pick(ray)?.id, 'b');
  viewer.setVisible(['b'], false);
  assert.equal(pick(ray)?.id, 'a');
  assert.equal(pick(ray)?.point.z, 2);
  viewer.setSectionBox({ min: [1000, 2000, 3003], max: [1010, 2010, 3010] });
  assert.equal(pick(ray), null);
});
