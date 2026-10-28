import test from 'node:test';
import assert from 'node:assert/strict';
import { Box3, BufferAttribute, BufferGeometry, Group, Matrix3, Matrix4, Mesh, MeshPhongMaterial, Ray, Vector3 } from 'three';
import { ModelViewer } from '../src/viewer/model-viewer.ts';
import { BoundsTree, TriangleIndex } from '../src/viewer/spatial.ts';

function fixture(instanced = false) {
  const viewer = Object.create(ModelViewer.prototype) as ModelViewer;
  const geometry = new BufferGeometry();
  geometry.setAttribute('position', new BufferAttribute(new Float32Array([-2, -2, 0, 2, -2, 0, 0, 2, 0]), 3));
  geometry.setIndex([0, 1, 2]);
  const matrix = instanced ? new Matrix4().makeRotationX(0.3).scale(new Vector3(2, 3, 4)).setPosition(0, 0, 5) : new Matrix4().makeTranslation(0, 0, 5);
  if (!instanced) geometry.applyMatrix4(matrix);
  geometry.computeBoundingBox();
  const bounds = instanced ? geometry.boundingBox!.clone().applyMatrix4(matrix) : geometry.boundingBox!.clone();
  const mesh = new Mesh(geometry, new MeshPhongMaterial());
  const batch = { mesh, bounds, owners: new Uint32Array([0]), componentIndices: [0], triangleIndex: new TriangleIndex(geometry),
    ...(instanced ? { instanceMatrices: [matrix], instanceTree: BoundsTree.fromBoxes([bounds]), instanceBounds: [bounds] } : {}) };
  const lastPick = { batches: 10, triangles: 20, milliseconds: 30 };
  Object.assign(viewer, { batches: [batch], batchTree: BoundsTree.fromBoxes([bounds]), componentIds: ['floor'], stateBytes: new Uint8Array([255, 0, 0, 255]),
    sectionBounds: null, sectionPlanes: [], bounds, lastPick });
  const pick = (ray: Ray, limit = Infinity, record = false) => (viewer as unknown as { pickRay(ray: Ray, limit: number, record: boolean): { id: string; point: Vector3; normal: Vector3 } | null }).pickRay(ray, limit, record);
  return { viewer, pick, matrix, geometry, mesh, lastPick };
}

test('ground ray queries use world distance and world normals for merged and scaled instances', () => {
  for (const instanced of [false, true]) {
    const f = fixture(instanced);
    const ray = new Ray(new Vector3(0, 0, 10), new Vector3(0, 0, -1));
    const hit = f.pick(ray, 5.01);
    assert.equal(hit?.id, 'floor');
    assert.ok(hit && Math.abs(hit.point.z - 5) < 1e-6);
    const expected = instanced ? new Vector3(0, 0, 1).applyNormalMatrix(new Matrix3().getNormalMatrix(f.matrix)) : new Vector3(0, 0, 1);
    assert.ok(hit && hit.normal.distanceTo(expected) < 1e-6);
    assert.equal(f.pick(ray, 4.99), null, 'ground/camera rays cannot hit beyond the requested physical limit');
    assert.deepEqual((f.viewer as unknown as { lastPick: unknown }).lastPick, f.lastPick, 'navigation does not overwrite user selection pick metrics');
    f.geometry.dispose(); f.mesh.material.dispose();
  }
});

test('hidden and clipped floor geometry is excluded from navigation ground queries', () => {
  const f = fixture();
  const ray = new Ray(new Vector3(0, 0, 10), new Vector3(0, 0, -1));
  assert.ok(f.pick(ray));
  Object.assign(f.viewer, { stateBytes: new Uint8Array([0, 0, 0, 255]) });
  assert.equal(f.pick(ray), null);
  Object.assign(f.viewer, { stateBytes: new Uint8Array([255, 0, 0, 255]), sectionBounds: new Box3(new Vector3(-1, -1, 6), new Vector3(1, 1, 8)),
    sectionPlanes: [{ distanceToPoint: (point: Vector3) => point.z - 6 }] });
  assert.equal(f.pick(ray), null);
  f.geometry.dispose(); f.mesh.material.dispose();
});

test('the generated person is outside BIM model geometry and its resources are disposed and recreated', () => {
  const viewer = Object.create(ModelViewer.prototype) as ModelViewer;
  const avatar = new Group(), modelRoot = new Group();
  Object.assign(viewer, { walkAvatar: avatar, modelRoot });
  const helpers = viewer as unknown as { createWalkAvatar(): void; clearWalkAvatar(): void };
  helpers.createWalkAvatar();
  assert.ok(avatar.children.length >= 6, 'head, torso and limbs are actual mesh geometry');
  assert.equal(modelRoot.children.length, 0, 'the person cannot become a BIM component or enter batch picking');
  const resources = new Set<MeshPhongMaterial | BufferGeometry>();
  avatar.traverse(object => {
    if (object instanceof Mesh) {
      resources.add(object.geometry);
      for (const material of Array.isArray(object.material) ? object.material : [object.material]) resources.add(material as MeshPhongMaterial);
    }
  });
  let disposed = 0;
  for (const resource of resources) resource.addEventListener('dispose', () => disposed++);
  helpers.clearWalkAvatar();
  assert.equal(disposed, resources.size, 'shared materials are released exactly once');
  assert.equal(avatar.children.length, 0); assert.equal(avatar.visible, false);
  helpers.createWalkAvatar(); assert.ok(avatar.children.length >= 6);
  helpers.clearWalkAvatar();
});
