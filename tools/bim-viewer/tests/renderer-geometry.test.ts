import test from 'node:test';
import assert from 'node:assert/strict';
import { Matrix4, Vector3 } from 'three';
import { composeTransform, geometryBounds, materialSignature, texturePathCandidates, textureTransform, vertexNormals } from '../src/viewer/geometry.ts';
import type { BimGeometry } from '../src/model.ts';

function triangle(): BimGeometry {
  return { id: 'triangle', positions: new Float32Array([0, 0, 0, 2, 0, 0, 0, 2, 0]), indices: new Uint32Array([0, 1, 2]) };
}

test('column-major component transform is applied after its local node transform', () => {
  const component = new Matrix4().makeRotationZ(Math.PI / 2).setPosition(100, 200, 300);
  const node = new Matrix4().makeTranslation(10, 0, 0);
  const point = new Vector3(1, 0, 0).applyMatrix4(composeTransform(component.elements, node.elements));
  assert.ok(point.distanceTo(new Vector3(100, 211, 300)) < 1e-9);
});

test('geometry bounds and missing normals are computed from the actual mesh', () => {
  const geometry = triangle();
  const box = geometryBounds(geometry);
  assert.deepEqual(box.min.toArray(), [0, 0, 0]);
  assert.deepEqual(box.max.toArray(), [2, 2, 0]);
  assert.deepEqual([...vertexNormals(geometry)], [0, 0, 1, 0, 0, 1, 0, 0, 1]);
});

test('existing normals are preserved, and degenerate faces remain finite', () => {
  const geometry = triangle();
  geometry.normals = new Float32Array([0, 0, -1, 0, 0, -1, 0, 0, -1]);
  assert.equal(vertexNormals(geometry), geometry.normals);
  delete geometry.normals;
  geometry.indices = new Uint32Array([0, 0, 0]);
  assert.ok([...vertexNormals(geometry)].every(Number.isFinite));
});

test('equivalent materials merge across source IDs without losing appearance distinctions', () => {
  assert.equal(materialSignature({ id: 'a', name: 'one', color: [1, 0, 0] }), materialSignature({ id: 'b', name: 'two', color: [1, 0, 0] }));
  assert.notEqual(materialSignature({ color: [1, 0, 0] }), materialSignature({ color: [0, 0, 1] }));
  assert.notEqual(materialSignature({ map: 'a.png' }), materialSignature({ map: 'b.png' }));
  assert.notEqual(materialSignature({ opacity: 1 }), materialSignature({ opacity: 0.5 }));
});

test('texture names resolve locally for Windows and ZIP paths', () => {
  assert.deepEqual(texturePathCandidates('C:\\Material\\Stone.PNG'), ['c:/material/stone.png', 'texture/stone.png', 'stone.png']);
  assert.deepEqual(texturePathCandidates('./texture/Stone.PNG'), ['texture/stone.png', 'stone.png']);
});

test('texture transforms keep exported radians and finite scales without guessing units', () => {
  const transform = textureTransform({ mapWAngle: Math.PI / 2, mapScaleX: 0, mapScaleY: -2, mapOffsetX: 0.25 }, 'map');
  assert.equal(transform.rotation, Math.PI / 2);
  assert.deepEqual(transform.repeat, [0, -2]);
  assert.deepEqual(transform.offset, [0.25, 0]);
  assert.equal(textureTransform({ bumpMapWAngle: Math.PI * 2 }, 'bumpMap').rotation, Math.PI * 2);
  assert.deepEqual(textureTransform({ mapScaleX: Infinity, mapOffsetY: NaN, mapWAngle: '90' }, 'map'), {
    repeat: [1, 1], offset: [0, 0], rotation: 0,
  });
});
