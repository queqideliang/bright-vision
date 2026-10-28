import { Box3, BufferGeometry, Ray, Vector3 } from 'three';

interface BoundsNode {
  box: Box3;
  left: number;
  right: number;
  start: number;
  end: number;
}

/** Entry distance (zero for an origin inside the box), unlike exit-only tests. */
export function rayBoxDistance(ray: Ray, box: Box3): number {
  let near = 0;
  let far = Infinity;
  for (const axis of ['x', 'y', 'z'] as const) {
    const direction = ray.direction[axis];
    const origin = ray.origin[axis];
    if (direction === 0) {
      if (origin < box.min[axis] || origin > box.max[axis]) return Infinity;
      continue;
    }
    const a = (box.min[axis] - origin) / direction;
    const b = (box.max[axis] - origin) / direction;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
    if (near > far) return Infinity;
  }
  return near;
}

/** Balanced median BVH. Input bounds are only retained while building it. */
export class BoundsTree {
  private readonly order: Uint32Array;
  private readonly nodes: BoundsNode[] = [];

  constructor(bounds: Float32Array | Float64Array, leafSize = 12) {
    if (bounds.length % 6 !== 0) throw new Error('Invalid spatial bounds.');
    const count = bounds.length / 6;
    this.order = Uint32Array.from({ length: count }, (_, i) => i);
    const coordinate = (id: number, axis: number) => bounds[id * 6 + axis]! + bounds[id * 6 + axis + 3]!;
    const swap = (a: number, b: number) => { const id = this.order[a]!; this.order[a] = this.order[b]!; this.order[b] = id; };
    const select = (start: number, end: number, middle: number, axis: number) => {
      let low = start, high = end - 1;
      while (low < high) {
        const pivot = coordinate(this.order[(low + high) >>> 1]!, axis);
        let left = low, right = high;
        while (left <= right) {
          while (coordinate(this.order[left]!, axis) < pivot) left++;
          while (coordinate(this.order[right]!, axis) > pivot) right--;
          if (left <= right) { swap(left, right); left++; right--; }
        }
        if (middle <= right) high = right;
        else if (middle >= left) low = left;
        else break;
      }
    };
    const build = (start: number, end: number): number => {
      const box = new Box3();
      for (let at = start; at < end; at++) {
        const offset = this.order[at]! * 6;
        box.min.x = Math.min(box.min.x, bounds[offset]!);
        box.min.y = Math.min(box.min.y, bounds[offset + 1]!);
        box.min.z = Math.min(box.min.z, bounds[offset + 2]!);
        box.max.x = Math.max(box.max.x, bounds[offset + 3]!);
        box.max.y = Math.max(box.max.y, bounds[offset + 4]!);
        box.max.z = Math.max(box.max.z, bounds[offset + 5]!);
      }
      const index = this.nodes.length;
      const node: BoundsNode = { box, start, end, left: -1, right: -1 };
      this.nodes.push(node);
      if (end - start > leafSize) {
        const size = box.getSize(new Vector3());
        const axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        const middle = (start + end) >>> 1;
        select(start, end, middle, axis);
        node.left = build(start, middle);
        node.right = build(middle, end);
      }
      return index;
    };
    if (count > 0) build(0, count);
  }

  static fromBoxes(boxes: readonly Box3[], leafSize = 12): BoundsTree {
    const bounds = new Float64Array(boxes.length * 6);
    boxes.forEach((box, index) => { box.min.toArray(bounds, index * 6); box.max.toArray(bounds, index * 6 + 3); });
    return new BoundsTree(bounds, leafSize);
  }

  get byteLength(): number { return this.order.byteLength + this.nodes.length * 80; }

  queryRay(ray: Ray, maxDistance = Infinity): number[] {
    const result: number[] = [];
    const stack = this.nodes.length ? [0] : [];
    while (stack.length) {
      const node = this.nodes[stack.pop()!]!;
      const distance = rayBoxDistance(ray, node.box);
      if (!Number.isFinite(distance) || distance > maxDistance) continue;
      if (node.left < 0) {
        for (let i = node.start; i < node.end; i++) result.push(this.order[i]!);
      } else stack.push(node.left, node.right);
    }
    return result;
  }

  queryBox(box: Box3): number[] {
    const result: number[] = [];
    const stack = this.nodes.length ? [0] : [];
    while (stack.length) {
      const node = this.nodes[stack.pop()!]!;
      if (!node.box.intersectsBox(box)) continue;
      if (node.left < 0) {
        for (let i = node.start; i < node.end; i++) result.push(this.order[i]!);
      } else stack.push(node.left, node.right);
    }
    return result;
  }
}

export interface TriangleHit {
  triangle: number;
  point: Vector3;
  normal: Vector3;
  distance: number;
}

/** Exact two-sided ray/triangle tests after a triangle-level broad phase. */
export class TriangleIndex {
  private readonly tree: BoundsTree;
  private readonly positions: ArrayLike<number>;
  private readonly indices: ArrayLike<number>;
  lastTriangleTests = 0;

  constructor(geometry: BufferGeometry) {
    this.positions = geometry.getAttribute('position').array;
    if (!geometry.index) throw new Error('Indexed geometry is required.');
    this.indices = geometry.index.array;
    const triangleCount = this.indices.length / 3;
    const bounds = new Float32Array(triangleCount * 6);
    for (let triangle = 0; triangle < triangleCount; triangle++) {
      for (let axis = 0; axis < 3; axis++) {
        const a = this.positions[this.indices[triangle * 3]! * 3 + axis]!;
        const b = this.positions[this.indices[triangle * 3 + 1]! * 3 + axis]!;
        const c = this.positions[this.indices[triangle * 3 + 2]! * 3 + axis]!;
        bounds[triangle * 6 + axis] = Math.min(a, b, c);
        bounds[triangle * 6 + axis + 3] = Math.max(a, b, c);
      }
    }
    this.tree = new BoundsTree(bounds, 12);
  }

  get byteLength(): number { return this.tree.byteLength; }

  raycast(ray: Ray, accept: (triangle: number, point: Vector3) => boolean, maxDistance = Infinity): TriangleHit | null {
    const a = new Vector3(), b = new Vector3(), c = new Vector3(), point = new Vector3();
    let result: TriangleHit | null = null;
    this.lastTriangleTests = 0;
    for (const triangle of this.tree.queryRay(ray, maxDistance)) {
      this.lastTriangleTests++;
      a.fromArray(this.positions, this.indices[triangle * 3]! * 3);
      b.fromArray(this.positions, this.indices[triangle * 3 + 1]! * 3);
      c.fromArray(this.positions, this.indices[triangle * 3 + 2]! * 3);
      if (!ray.intersectTriangle(a, b, c, false, point)) continue;
      const distance = point.distanceTo(ray.origin);
      if (distance > maxDistance || !accept(triangle, point)) continue;
      maxDistance = distance;
      result = { triangle, point: point.clone(), distance, normal: b.clone().sub(a).cross(c.clone().sub(a)).normalize() };
    }
    return result;
  }
}

export function canInstanceTransform(matrix: ArrayLike<number>): boolean {
  if (matrix.length !== 16 || Array.from(matrix).some(value => !Number.isFinite(value))
    || [3, 7, 11].some(index => Math.abs(matrix[index]!) > 1e-12) || Math.abs(matrix[15]! - 1) > 1e-12) return false;
  const a = new Vector3().fromArray(matrix, 0), b = new Vector3().fromArray(matrix, 4), c = new Vector3().fromArray(matrix, 8);
  const determinant = a.dot(b.clone().cross(c));
  if (!Number.isFinite(determinant) || determinant <= 1e-12) return false;
  const tolerance = 1e-7;
  return Math.abs(a.dot(b)) <= tolerance * a.length() * b.length()
    && Math.abs(a.dot(c)) <= tolerance * a.length() * c.length()
    && Math.abs(b.dot(c)) <= tolerance * b.length() * c.length();
}
