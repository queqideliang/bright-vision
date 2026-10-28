import { Vector3 } from 'three';

export type NavigationMode = 'orbit' | 'walk' | 'fly';
export type NavigationUnit = 'm' | 'mm' | 'ft' | 'unknown';
export interface NavigationState {
  mode: NavigationMode;
  unit: NavigationUnit;
  speedMetersPerSecond: number;
  eyeHeightMeters: number;
  active: boolean;
  avatarVisible: boolean;
  needsGroundPlacement: boolean;
  groundStatus: 'none' | 'ready' | 'blocked';
  available: boolean;
  reason: 'no-model' | 'unknown-unit' | null;
}

export interface NavigationSurface { point: Vector3; normal: Vector3 }
export type GroundSampler = (feet: Vector3, maximumRise: number, maximumDrop: number) => NavigationSurface | null;
export type WalkObstacleTest = (from: Vector3, to: Vector3) => boolean;

/** Explicit source units are required: extents cannot establish the real scale. */
export function navigationUnitsPerMeter(unit: NavigationUnit): number | null {
  if (unit === 'm') return 1;
  if (unit === 'mm') return 1000;
  if (unit === 'ft') return 1 / 0.3048;
  return null;
}

/** Z-up yaw/pitch, without roll. Zero yaw faces +X. */
export function navigationDirection(yaw: number, pitch: number): Vector3 {
  return new Vector3(Math.cos(yaw) * Math.cos(pitch), Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch));
}

export function navigationAngles(direction: Vector3): { yaw: number; pitch: number } {
  const length = direction.length();
  if (!Number.isFinite(length) || length < 1e-12) return { yaw: 0, pitch: 0 };
  return { yaw: Math.atan2(direction.y, direction.x), pitch: Math.asin(Math.max(-1, Math.min(1, direction.z / length))) };
}

/** Normalize simultaneous axes so diagonal travel is no faster than a single key. */
export function navigationDisplacement(options: {
  mode: 'walk' | 'fly'; yaw: number; pitch: number; forward: number; right: number; up: number;
  speedMetersPerSecond: number; unitsPerMeter: number; seconds: number; fast: boolean;
}): Vector3 {
  const { mode, yaw, pitch, forward, right, up, speedMetersPerSecond, unitsPerMeter, seconds, fast } = options;
  if (![yaw, pitch, forward, right, up, speedMetersPerSecond, unitsPerMeter, seconds].every(Number.isFinite)
    || unitsPerMeter <= 0 || speedMetersPerSecond <= 0 || seconds <= 0) return new Vector3();
  const result = navigationDirection(yaw, mode === 'walk' ? 0 : pitch).multiplyScalar(forward);
  result.addScaledVector(new Vector3(Math.sin(yaw), -Math.cos(yaw), 0), right);
  if (mode === 'fly') result.z += up;
  if (result.lengthSq() > 1) result.normalize();
  // A stalled/hidden frame must not teleport a user through geometry on resume.
  return result.multiplyScalar(speedMetersPerSecond * unitsPerMeter * Math.min(seconds, 0.05) * (fast ? 3 : 1));
}

export function isWalkableSurface(normal: Vector3): boolean {
  const length = normal.length();
  // Imports may reverse winding; slope uses the absolute vertical component.
  return Number.isFinite(length) && length > 1e-12 && Math.abs(normal.z) / length >= Math.SQRT1_2 - 1e-10;
}

/**
 * Limited ground following, not a physics/capsule solver. Short substeps avoid
 * crossing a gap merely because both endpoints happen to land on a surface.
 */
export function resolveWalkMovement(
  feet: Vector3, displacement: Vector3, unitsPerMeter: number, sampleGround: GroundSampler,
  blockedByObstacle?: WalkObstacleTest,
): { position: Vector3; blocked: boolean } {
  const position = feet.clone();
  const length = Math.hypot(displacement.x, displacement.y);
  if (!Number.isFinite(length) || !Number.isFinite(unitsPerMeter) || unitsPerMeter <= 0) return { position, blocked: true };
  if (length === 0) return { position, blocked: false };
  const steps = Math.ceil(length / (0.1 * unitsPerMeter));
  // Public input is bounded as well as the controls: do not permit unbounded work.
  if (steps > 64) return { position, blocked: true };
  const step = new Vector3(displacement.x / steps, displacement.y / steps, 0);
  for (let at = 0; at < steps; at++) {
    const next = position.clone().add(step);
    const ground = sampleGround(next, 0.3 * unitsPerMeter, 0.5 * unitsPerMeter);
    const rise = ground ? ground.point.z - position.z : Infinity;
    if (!ground || !isWalkableSurface(ground.normal) || !Number.isFinite(ground.point.z)
      || rise > 0.3 * unitsPerMeter + unitsPerMeter * 1e-6 || rise < -0.5 * unitsPerMeter - unitsPerMeter * 1e-6)
      return { position, blocked: true };
    next.z = ground.point.z;
    if (blockedByObstacle?.(position, next)) return { position, blocked: true };
    position.copy(next);
  }
  return { position, blocked: false };
}
