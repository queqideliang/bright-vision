import { PerspectiveCamera, Vector3 } from 'three';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import {
  isWalkableSurface, navigationAngles, navigationDirection, navigationDisplacement,
  navigationUnitsPerMeter, resolveWalkMovement,
} from './navigation.ts';
import type { GroundSampler, NavigationMode, NavigationState, NavigationSurface, NavigationUnit, WalkObstacleTest } from './navigation.ts';

interface NavigationOptions {
  canvas: HTMLCanvasElement;
  camera: PerspectiveCamera;
  orbit: OrbitControls;
  sampleGround: GroundSampler;
  blockedByObstacle: WalkObstacleTest;
  cameraObstruction: (from: Vector3, to: Vector3) => NavigationSurface | null;
  onWalkPosition: (feet: Vector3, yaw: number, unitsPerMeter: number, eyeHeightMeters: number) => void;
  onStateChange: (state: NavigationState) => void;
  onRender: () => void;
}

/** Canvas-scoped navigation; no pointer lock, OS input hooks or global hotkeys. */
export class NavigationControls {
  private readonly options: NavigationOptions;
  private readonly canvas: HTMLCanvasElement;
  private readonly document: Document;
  private readonly window: Window;
  private mode: NavigationMode = 'orbit';
  private unit: NavigationUnit = 'unknown';
  private hasModel = false;
  private speed = 1.4;
  private eyeHeight = 1.7;
  private active = false;
  private needsGroundPlacement = false;
  private groundStatus: NavigationState['groundStatus'] = 'none';
  private readonly keys = new Set<string>();
  private drag: { id: number; x: number; y: number } | null = null;
  private feet: Vector3 | null = null;
  private avatarYaw = 0;
  private yaw = 0;
  private pitch = 0;
  private lookDistance = 1;
  private previousNear = 0.1;
  private frame = 0;
  private previousTime: number | null = null;
  private disposed = false;

  constructor(options: NavigationOptions) {
    this.options = options;
    this.canvas = options.canvas;
    this.document = this.canvas.ownerDocument;
    const view = this.document.defaultView;
    if (!view) throw new Error('Navigation requires a browser window.');
    this.window = view;
    this.canvas.addEventListener('pointerdown', this.handlePointerDown);
    this.canvas.addEventListener('pointermove', this.handlePointerMove);
    this.canvas.addEventListener('pointerup', this.handlePointerUp);
    this.canvas.addEventListener('pointercancel', this.handlePointerCancel);
    this.canvas.addEventListener('lostpointercapture', this.handleLostPointerCapture);
    this.canvas.addEventListener('contextmenu', this.handleContextMenu);
    this.canvas.addEventListener('keydown', this.handleKeyDown);
    this.canvas.addEventListener('blur', this.pause);
    this.window.addEventListener('keyup', this.handleKeyUp);
    this.window.addEventListener('blur', this.pause);
    this.document.addEventListener('focusin', this.handleFocusIn);
    this.document.addEventListener('pointerdown', this.handleDocumentPointerDown, true);
    this.document.addEventListener('visibilitychange', this.handleVisibilityChange);
  }

  getState(): NavigationState {
    const reason = !this.hasModel ? 'no-model' : navigationUnitsPerMeter(this.unit) === null ? 'unknown-unit' : null;
    return {
      mode: this.mode, unit: this.unit, speedMetersPerSecond: this.speed, eyeHeightMeters: this.eyeHeight,
      active: this.active, avatarVisible: this.mode === 'walk' && this.feet !== null,
      needsGroundPlacement: this.needsGroundPlacement, groundStatus: this.groundStatus,
      available: reason === null, reason,
    };
  }

  setModelAvailable(available: boolean): void {
    if (!available) this.setMode('orbit');
    this.hasModel = available;
    this.emit();
  }

  setUnit(unit: NavigationUnit): void {
    if (!['m', 'mm', 'ft', 'unknown'].includes(unit)) throw new Error('Unsupported navigation unit.');
    if (unit === this.unit) return;
    // Reinterpreting coordinates while walking would teleport/resize the person.
    this.setMode('orbit');
    this.unit = unit;
    this.emit();
  }

  setSpeed(metersPerSecond: number): void {
    if (!Number.isFinite(metersPerSecond) || metersPerSecond < 0.1 || metersPerSecond > 10)
      throw new Error('Navigation speed must be between 0.1 and 10 m/s.');
    this.speed = metersPerSecond;
    this.emit();
  }

  setEyeHeight(meters: number): void {
    if (!Number.isFinite(meters) || meters < 0.8 || meters > 2.2) throw new Error('Eye height must be between 0.8 and 2.2 m.');
    this.eyeHeight = meters;
    if (this.mode === 'walk' && this.feet) this.updateCamera();
    this.emit();
  }

  setMode(mode: NavigationMode): boolean {
    if (!['orbit', 'walk', 'fly'].includes(mode)) throw new Error('Unsupported navigation mode.');
    if (this.disposed || (mode !== 'orbit' && !this.getState().available)) return false;
    if (mode === this.mode) return true;
    this.pause();
    const { camera, orbit } = this.options;
    if (this.mode === 'orbit') this.previousNear = camera.near;
    const direction = camera.getWorldDirection(new Vector3());
    const angles = navigationAngles(direction);
    this.yaw = angles.yaw;
    this.pitch = Math.max(-1.3, Math.min(1.3, angles.pitch));
    this.lookDistance = Math.max(navigationUnitsPerMeter(this.unit) ?? 1, camera.position.distanceTo(orbit.target));
    this.mode = mode;
    this.feet = null;
    this.needsGroundPlacement = mode === 'walk';
    this.groundStatus = 'none';
    orbit.enabled = mode === 'orbit';
    if (mode === 'orbit') {
      const distance = Math.max(camera.position.distanceTo(orbit.target), 1e-8);
      camera.near = Math.min(this.previousNear, distance / 1000);
      orbit.minDistance = Math.min(orbit.minDistance, distance / 1000);
      orbit.maxDistance = Math.max(orbit.maxDistance, distance * 50);
      camera.updateProjectionMatrix();
      orbit.update();
    } else {
      const scale = navigationUnitsPerMeter(this.unit)!;
      camera.near = 0.02 * scale;
      camera.far = Math.max(camera.far, 100 * scale);
      camera.updateProjectionMatrix();
      if (mode === 'walk') this.pitch = Math.max(-1.15, Math.min(0.12, this.pitch));
      else this.updateCamera();
    }
    this.emit();
    this.options.onRender();
    return true;
  }

  /** Returns false for a wall/steep face; an ordinary selection is never implied. */
  placeOnGround(surface: NavigationSurface): boolean {
    if (this.mode !== 'walk' || !this.needsGroundPlacement || !isWalkableSurface(surface.normal)) return false;
    const scale = navigationUnitsPerMeter(this.unit)!;
    if (![surface.point.x, surface.point.y, surface.point.z].every(Number.isFinite)) return false;
    const ground = this.options.sampleGround(surface.point, 0.02 * scale, 0.02 * scale);
    if (!ground || !isWalkableSurface(ground.normal)) return false;
    this.feet = ground.point.clone();
    this.avatarYaw = this.yaw;
    this.needsGroundPlacement = false;
    this.groundStatus = 'ready';
    this.updateCamera();
    this.emit();
    return true;
  }

  resetWalkPlacement(): void {
    if (this.mode !== 'walk') return;
    this.pause();
    this.feet = null;
    this.needsGroundPlacement = true;
    this.groundStatus = 'none';
    this.emit();
  }

  private emit(): void { this.options.onStateChange(this.getState()); }

  readonly pause = (): void => {
    this.keys.clear();
    this.previousTime = null;
    if (this.frame) this.window.cancelAnimationFrame(this.frame);
    this.frame = 0;
    const drag = this.drag;
    this.drag = null;
    if (drag && this.canvas.hasPointerCapture(drag.id)) this.canvas.releasePointerCapture(drag.id);
    if (this.active) { this.active = false; this.emit(); }
  };

  private readonly handleFocusIn = (event: FocusEvent): void => {
    if (event.target !== this.canvas) this.pause();
  };

  private readonly handleDocumentPointerDown = (event: PointerEvent): void => {
    // A click on a nonfocusable panel still relinquishes canvas navigation.
    if (event.target !== this.canvas) this.pause();
  };

  private readonly handleVisibilityChange = (): void => {
    if (this.document.hidden) this.pause();
  };

  private readonly handlePointerDown = (event: PointerEvent): void => {
    if (this.mode === 'orbit' || !event.isPrimary || this.document.hidden || (event.button !== 0 && event.button !== 2)) return;
    this.canvas.focus({ preventScroll: true });
    this.active = this.document.activeElement === this.canvas;
    this.emit();
    if (event.button !== 2 || this.needsGroundPlacement) return;
    event.preventDefault();
    this.drag = { id: event.pointerId, x: event.clientX, y: event.clientY };
    this.canvas.setPointerCapture(event.pointerId);
  };

  private readonly handlePointerMove = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!this.active || !drag || drag.id !== event.pointerId || this.mode === 'orbit') return;
    this.yaw -= (event.clientX - drag.x) * 0.003;
    const limit = this.mode === 'walk' ? { min: -1.15, max: 0.12 } : { min: -1.3, max: 1.3 };
    this.pitch = Math.max(limit.min, Math.min(limit.max, this.pitch - (event.clientY - drag.y) * 0.003));
    drag.x = event.clientX; drag.y = event.clientY;
    this.updateCamera();
  };

  private readonly handlePointerUp = (event: PointerEvent): void => {
    if (this.drag?.id !== event.pointerId) return;
    const drag = this.drag;
    this.drag = null;
    if (this.canvas.hasPointerCapture(drag.id)) this.canvas.releasePointerCapture(drag.id);
  };

  private readonly handlePointerCancel = (): void => { this.pause(); };
  private readonly handleLostPointerCapture = (event: PointerEvent): void => {
    if (this.drag?.id === event.pointerId) this.pause();
  };

  private readonly handleContextMenu = (event: Event): void => {
    if (this.mode !== 'orbit') event.preventDefault();
  };

  private readonly handleKeyDown = (event: KeyboardEvent): void => {
    if (this.mode === 'orbit' || event.target !== this.canvas || this.document.activeElement !== this.canvas) return;
    if (event.code === 'Escape') { event.preventDefault(); this.setMode('orbit'); return; }
    if (event.ctrlKey || event.metaKey || event.altKey || event.isComposing) { this.pause(); return; }
    const codes = this.mode === 'fly'
      ? ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE', 'ShiftLeft', 'ShiftRight']
      : ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ShiftLeft', 'ShiftRight'];
    if (!this.active || this.needsGroundPlacement || !codes.includes(event.code)) return;
    event.preventDefault();
    this.keys.add(event.code);
    this.requestFrame();
  };

  private readonly handleKeyUp = (event: KeyboardEvent): void => { this.keys.delete(event.code); };

  private hasMovement(): boolean {
    return ['KeyW', 'KeyA', 'KeyS', 'KeyD', ...(this.mode === 'fly' ? ['KeyQ', 'KeyE'] : [])].some(code => this.keys.has(code));
  }

  private requestFrame(): void {
    if (!this.frame && !this.disposed && this.active && this.hasMovement()) this.frame = this.window.requestAnimationFrame(this.tick);
  }

  private readonly tick = (timestamp: number): void => {
    this.frame = 0;
    if (!this.active || this.mode === 'orbit' || this.document.activeElement !== this.canvas || this.document.hidden) { this.pause(); return; }
    const seconds = this.previousTime === null ? 0 : (timestamp - this.previousTime) / 1000;
    this.previousTime = timestamp;
    const scale = navigationUnitsPerMeter(this.unit)!;
    const displacement = navigationDisplacement({ mode: this.mode, yaw: this.yaw, pitch: this.pitch,
      forward: Number(this.keys.has('KeyW')) - Number(this.keys.has('KeyS')),
      right: Number(this.keys.has('KeyD')) - Number(this.keys.has('KeyA')),
      up: Number(this.keys.has('KeyE')) - Number(this.keys.has('KeyQ')),
      speedMetersPerSecond: this.speed, unitsPerMeter: scale, seconds,
      fast: this.keys.has('ShiftLeft') || this.keys.has('ShiftRight') });
    if (displacement.lengthSq() > 0) {
      if (this.mode === 'fly') this.options.camera.position.add(displacement);
      else if (this.feet) {
        const next = resolveWalkMovement(this.feet, displacement, scale, this.options.sampleGround, this.options.blockedByObstacle);
        const moved = next.position.clone().sub(this.feet);
        if (moved.lengthSq() > scale * scale * 1e-12) this.avatarYaw = Math.atan2(moved.y, moved.x);
        this.feet.copy(next.position);
        const status = next.blocked ? 'blocked' : 'ready';
        if (status !== this.groundStatus) { this.groundStatus = status; this.emit(); }
      }
      this.updateCamera();
    }
    if (this.hasMovement()) this.requestFrame();
    else this.previousTime = null;
  };

  private updateCamera(): void {
    const { camera, orbit } = this.options;
    const direction = navigationDirection(this.yaw, this.pitch);
    if (this.mode === 'walk' && this.feet) {
      const scale = navigationUnitsPerMeter(this.unit)!;
      this.options.onWalkPosition(this.feet, this.avatarYaw, scale, this.eyeHeight);
      const target = this.feet.clone().add(new Vector3(0, 0, this.eyeHeight * 0.72 * scale));
      const desired = target.clone().addScaledVector(direction, -4 * scale);
      const obstruction = this.options.cameraObstruction(target, desired);
      if (obstruction) {
        const distance = Math.max(0.08 * scale, target.distanceTo(obstruction.point) - 0.15 * scale);
        desired.copy(target).addScaledVector(direction, -distance);
      }
      camera.position.copy(desired);
      orbit.target.copy(target);
    } else if (this.mode === 'fly') orbit.target.copy(camera.position).addScaledVector(direction, this.lookDistance);
    else return;
    camera.lookAt(orbit.target);
    this.options.onRender();
  }

  dispose(): void {
    if (this.disposed) return;
    this.setMode('orbit');
    this.pause();
    this.disposed = true;
    this.canvas.removeEventListener('pointerdown', this.handlePointerDown);
    this.canvas.removeEventListener('pointermove', this.handlePointerMove);
    this.canvas.removeEventListener('pointerup', this.handlePointerUp);
    this.canvas.removeEventListener('pointercancel', this.handlePointerCancel);
    this.canvas.removeEventListener('lostpointercapture', this.handleLostPointerCapture);
    this.canvas.removeEventListener('contextmenu', this.handleContextMenu);
    this.canvas.removeEventListener('keydown', this.handleKeyDown);
    this.canvas.removeEventListener('blur', this.pause);
    this.window.removeEventListener('keyup', this.handleKeyUp);
    this.window.removeEventListener('blur', this.pause);
    this.document.removeEventListener('focusin', this.handleFocusIn);
    this.document.removeEventListener('pointerdown', this.handleDocumentPointerDown, true);
    this.document.removeEventListener('visibilitychange', this.handleVisibilityChange);
  }
}
