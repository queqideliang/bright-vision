import type { QuantitySnapshot } from './quantities.ts';

export type PropertyGroups = Record<string, Record<string, unknown>>;

export interface BimComponent {
  id: string;
  name: string;
  category: string;
  categoryCode?: string;
  family: string;
  type: string;
  matrix: Float64Array;
  nodeIds: string[];
  properties: PropertyGroups;
  typeProperties: PropertyGroups;
  familyProperties: PropertyGroups;
  levelNames: string[];
  quantitySnapshot?: QuantitySnapshot;
  quantityIssue?: string;
  min?: number[];
  max?: number[];
}

export interface BimGeometry {
  id: string;
  positions: Float32Array;
  indices: Uint32Array;
  normals?: Float32Array;
  uv?: Float32Array;
}

export interface BimNode {
  id: string;
  geometryId: string;
  materialId: string;
  matrix: Float64Array;
}

export interface BimMaterial {
  id: string;
  raw: Record<string, unknown>;
}

export interface BimLevel {
  name: string;
  elevation?: number;
  componentIds: string[];
}

export interface BimModel {
  name: string;
  units?: { length: 'm' | 'mm' | 'ft' | 'unknown'; source?: string };
  components: BimComponent[];
  geometries: Record<string, BimGeometry>;
  nodes: Record<string, BimNode>;
  materials: Record<string, BimMaterial>;
  textures: Record<string, { bytes: Uint8Array; mimeType: string }>;
  levels: BimLevel[];
  warnings: string[];
  stats: { components: number; geometries: number; triangles: number; nodes: number };
}

export interface LoadProgress {
  phase: string;
  progress: number;
}
