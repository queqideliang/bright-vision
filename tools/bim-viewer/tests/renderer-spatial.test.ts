import test from 'node:test';
import assert from 'node:assert/strict';
import { Box3, BufferAttribute, BufferGeometry, Matrix4, Ray, Vector3 } from 'three';
import { BoundsTree, TriangleIndex, canInstanceTransform, rayBoxDistance } from '../src/viewer/spatial.ts';

function geometry(positions: number[]): BufferGeometry {
  const result = new BufferGeometry();
  result.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3));
  result.setIndex(new BufferAttribute(Uint32Array.from({ length: positions.length / 3 }, (_, i) => i), 1));
  return result;
}

test('slab tests handle origins inside, parallel rays, tangency and boxes behind the ray', () => {
  const box = new Box3(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
  assert.equal(rayBoxDistance(new Ray(new Vector3(0, 0, 0), new Vector3(1, 0, 0)), box), 0);
  assert.equal(rayBoxDistance(new Ray(new Vector3(0, 1, 4), new Vector3(0, 0, -1)), box), 3);
  assert.equal(rayBoxDistance(new Ray(new Vector3(2, 0, 4), new Vector3(0, 0, -1)), box), Infinity);
  assert.equal(rayBoxDistance(new Ray(new Vector3(0, 0, 4), new Vector3(0, 0, 1)), box), Infinity);
  assert.deepEqual(BoundsTree.fromBoxes([box]).queryRay(new Ray(new Vector3(2, 0, 4), new Vector3(0, 0, -1))), []);
});

test('an empty spatial index has no candidates', () => {
  const tree = BoundsTree.fromBoxes([]);
  assert.deepEqual(tree.queryRay(new Ray()), []);
  assert.deepEqual(tree.queryBox(new Box3(new Vector3(), new Vector3(1, 1, 1))), []);
});

test('rejected hidden or clipped nearest triangles do not occlude a later visible hit', () => {
  const source = geometry([0, 0, 4, 2, 0, 4, 0, 2, 4, 0, 0, 2, 2, 0, 2, 0, 2, 2]);
  const index = new TriangleIndex(source);
  const ray = new Ray(new Vector3(0.25, 0.25, 10), new Vector3(0, 0, -1));
  assert.equal(index.raycast(ray, () => true)?.triangle, 0);
  assert.equal(index.raycast(ray, triangle => triangle !== 0)?.triangle, 1);
  assert.equal(index.raycast(ray, (_triangle, point) => point.z <= 3)?.distance, 8);
  assert.equal(index.raycast(ray, () => false), null);
});

test('BVH closest hits match brute-force two-sided ray intersections over varied rays', () => {
  let seed = 42;
  const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 0x100000000; };
  const positions: number[] = [];
  for (let i = 0; i < 700; i++) {
    const x = random() * 30 - 15, y = random() * 30 - 15, z = random() * 10;
    positions.push(x, y, z, x + 0.8, y, z + 0.1, x, y + 0.8, z - 0.1);
  }
  const source = geometry(positions), index = new TriangleIndex(source);
  const a = new Vector3(), b = new Vector3(), c = new Vector3(), point = new Vector3();
  for (let sample = 0; sample < 150; sample++) {
    const ray = new Ray(new Vector3(random() * 30 - 15, random() * 30 - 15, 20), new Vector3(random() * 0.05, random() * 0.05, -1).normalize());
    let nearest = Infinity;
    for (let i = 0; i < positions.length; i += 9) {
      if ((i / 9) % 3 === 0) continue;
      a.fromArray(positions, i); b.fromArray(positions, i + 3); c.fromArray(positions, i + 6);
      if (ray.intersectTriangle(a, b, c, false, point)) nearest = Math.min(nearest, ray.origin.distanceTo(point));
    }
    const hit = index.raycast(ray, triangle => triangle % 3 !== 0);
    if (nearest === Infinity) assert.equal(hit, null);
    else assert.ok(hit && Math.abs(hit.distance - nearest) < 1e-4);
  }
});

test('sparse geometry queries visit a bounded subset of triangles', () => {
  const positions: number[] = [];
  for (let y = 0; y < 100; y++) for (let x = 0; x < 100; x++) positions.push(x * 3, y * 3, 0, x * 3 + 1, y * 3, 0, x * 3, y * 3 + 1, 0);
  const index = new TriangleIndex(geometry(positions));
  assert.equal(index.raycast(new Ray(new Vector3(0.2, 0.2, 4), new Vector3(0, 0, -1)), () => true)?.triangle, 0);
  assert.ok(index.lastTriangleTests < 100, `tested ${index.lastTriangleTests} of 10000 triangles`);
});

test('instance route admits positive orthogonal transforms and retains mirrors/shears in merged route', () => {
  assert.equal(canInstanceTransform(new Matrix4().makeRotationZ(0.4).scale(new Vector3(2, 3, 4)).elements), true);
  assert.equal(canInstanceTransform(new Matrix4().makeScale(-1, 1, 1).elements), false);
  assert.equal(canInstanceTransform(new Matrix4().makeScale(0, 1, 1).elements), false);
  assert.equal(canInstanceTransform(new Matrix4().makeShear(0.2, 0, 0, 0, 0, 0).elements), false);
});
