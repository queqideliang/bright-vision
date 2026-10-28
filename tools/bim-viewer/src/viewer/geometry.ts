import { Box3, Matrix4, Vector3 } from 'three';
import type { BimGeometry } from '../model.ts';

/** The model stores column-major matrices; the node is local to its component. */
export function composeTransform(component: ArrayLike<number>, node: ArrayLike<number>, target = new Matrix4()): Matrix4 {
  return target.fromArray(component).multiply(new Matrix4().fromArray(node));
}

export function geometryBounds(geometry: BimGeometry): Box3 {
  const result = new Box3();
  const point = new Vector3();
  for (let i = 0; i < geometry.positions.length; i += 3) {
    result.expandByPoint(point.set(geometry.positions[i]!, geometry.positions[i + 1]!, geometry.positions[i + 2]!));
  }
  return result;
}

/** Derive shared vertex normals once per source geometry, before world transforms. */
export function vertexNormals(geometry: BimGeometry): Float32Array {
  if (geometry.normals?.length === geometry.positions.length) return geometry.normals;
  const positions = geometry.positions;
  const normals = new Float32Array(positions.length);
  const indices = geometry.indices;
  for (let i = 0; i < indices.length; i += 3) {
    const a = indices[i]! * 3;
    const b = indices[i + 1]! * 3;
    const c = indices[i + 2]! * 3;
    const ux = positions[b]! - positions[a]!;
    const uy = positions[b + 1]! - positions[a + 1]!;
    const uz = positions[b + 2]! - positions[a + 2]!;
    const vx = positions[c]! - positions[a]!;
    const vy = positions[c + 1]! - positions[a + 1]!;
    const vz = positions[c + 2]! - positions[a + 2]!;
    const nx = uy * vz - uz * vy;
    const ny = uz * vx - ux * vz;
    const nz = ux * vy - uy * vx;
    normals[a] = normals[a]! + nx;
    normals[a + 1] = normals[a + 1]! + ny;
    normals[a + 2] = normals[a + 2]! + nz;
    normals[b] = normals[b]! + nx;
    normals[b + 1] = normals[b + 1]! + ny;
    normals[b + 2] = normals[b + 2]! + nz;
    normals[c] = normals[c]! + nx;
    normals[c + 1] = normals[c + 1]! + ny;
    normals[c + 2] = normals[c + 2]! + nz;
  }
  for (let i = 0; i < normals.length; i += 3) {
    const length = Math.hypot(normals[i]!, normals[i + 1]!, normals[i + 2]!);
    if (length > 0) {
      normals[i] = normals[i]! / length;
      normals[i + 1] = normals[i + 1]! / length;
      normals[i + 2] = normals[i + 2]! / length;
    }
  }
  return normals;
}

export function texturePathCandidates(value: string): string[] {
  const path = value.replaceAll('\\', '/').replace(/^\.\//, '');
  const name = path.split('/').at(-1) ?? path;
  return [...new Set([path, `texture/${name}`, name])].map((item) => item.toLowerCase());
}

/** Exported WAngle values are radians. Keep finite exported scales, including zero. */
export function textureTransform(raw: Record<string, unknown>, prefix: string): {
  repeat: [number, number]; offset: [number, number]; rotation: number;
} {
  const number = (suffix: string, fallback: number): number => {
    const value = raw[`${prefix}${suffix}`];
    return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
  };
  return {
    repeat: [number('ScaleX', 1), number('ScaleY', 1)],
    offset: [number('OffsetX', 0), number('OffsetY', 0)],
    rotation: number('WAngle', 0),
  };
}

/** IDs and display names do not change the appearance and need not create draw calls. */
export function materialSignature(raw: Record<string, unknown>): string {
  return JSON.stringify([
    raw.color ?? null, raw.opacity ?? 1, raw.specular ?? null,
    raw.shininess ?? null, raw.emissive ?? null, raw.emissiveIntensity ?? null,
    raw.map ?? null, raw.mapScaleX ?? 1, raw.mapScaleY ?? 1,
    raw.mapOffsetX ?? 0, raw.mapOffsetY ?? 0, raw.mapWAngle ?? 0,
    raw.bumpMap ?? null, raw.bumpScale ?? 1,
    raw.bumpMapScaleX ?? 1, raw.bumpMapScaleY ?? 1,
    raw.bumpMapOffsetX ?? 0, raw.bumpMapOffsetY ?? 0, raw.bumpMapWAngle ?? 0,
  ]);
}
