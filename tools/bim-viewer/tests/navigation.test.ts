import test from 'node:test';
import assert from 'node:assert/strict';
import { PerspectiveCamera, Vector3 } from 'three';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { NavigationControls } from '../src/viewer/navigation-controls.ts';
import {
  isWalkableSurface, navigationAngles, navigationDirection, navigationDisplacement,
  navigationUnitsPerMeter, resolveWalkMovement,
} from '../src/viewer/navigation.ts';
import type { GroundSampler, NavigationState } from '../src/viewer/navigation.ts';

function near(actual: number, expected: number, epsilon = 1e-9): void {
  assert.ok(Math.abs(actual - expected) < epsilon, `${actual} should be ${expected}`);
}

test('navigation maintains a Z-up heading and clamps movement after a stalled frame', () => {
  near(navigationDirection(0, 0).distanceTo(new Vector3(1, 0, 0)), 0);
  near(navigationDirection(Math.PI / 2, 0).distanceTo(new Vector3(0, 1, 0)), 0);
  const angles = navigationAngles(navigationDirection(-0.7, 0.9));
  near(angles.yaw, -0.7); near(angles.pitch, 0.9);
  const movement = { mode: 'walk' as const, yaw: 0, pitch: 1.1, forward: 1, right: 0, up: 1,
    speedMetersPerSecond: 2, unitsPerMeter: 1, seconds: 5, fast: false };
  const step = navigationDisplacement(movement);
  near(step.x, 0.1); near(step.y, 0); near(step.z, 0);
  near(navigationDisplacement({ ...movement, right: 1 }).length(), step.length());
  near(navigationDisplacement({ ...movement, fast: true }).length(), step.length() * 3);
  near(navigationDisplacement({ ...movement, speedMetersPerSecond: NaN }).length(), 0);
  near(navigationDisplacement({ ...movement, seconds: -1 }).length(), 0);
});

test('physical speed is equal for explicitly declared meters, millimeters and feet', () => {
  assert.equal(navigationUnitsPerMeter('unknown'), null);
  for (const unit of ['m', 'mm', 'ft'] as const) {
    const scale = navigationUnitsPerMeter(unit)!;
    let position = new Vector3();
    for (let i = 0; i < 20; i++) position.add(navigationDisplacement({ mode: 'fly', yaw: 0, pitch: Math.PI / 4,
      forward: 1, right: 1, up: 0, speedMetersPerSecond: 1.4, unitsPerMeter: scale, seconds: 0.05, fast: false }));
    near(position.length() / scale, 1.4);
    assert.ok(position.z > 0, 'flight follows the pitched camera direction');
  }
});

test('walking follows actual surfaces, accepts bounded steps and stops at steep faces or missing floor', () => {
  const feet = new Vector3(0, 0, 0);
  const up = new Vector3(0, 0, 1);
  const floor: GroundSampler = at => ({ point: new Vector3(at.x, at.y, at.x >= 0.2 ? 0.25 : 0), normal: up });
  const step = resolveWalkMovement(feet, new Vector3(0.4, 0, 8), 1, floor);
  assert.equal(step.blocked, false); near(step.position.x, 0.4); near(step.position.z, 0.25);
  assert.deepEqual(feet.toArray(), [0, 0, 0], 'caller position is not mutated');
  const high = resolveWalkMovement(feet, new Vector3(0.4, 0, 0), 1, at => ({ point: new Vector3(at.x, at.y, 0.31), normal: up }));
  assert.equal(high.blocked, true); assert.deepEqual(high.position.toArray(), feet.toArray());
  const fall = resolveWalkMovement(feet, new Vector3(0.4, 0, 0), 1, at => ({ point: new Vector3(at.x, at.y, -0.51), normal: up }));
  assert.equal(fall.blocked, true);
  const gap = resolveWalkMovement(feet, new Vector3(1, 0, 0), 1, at => at.x >= 0.4 && at.x <= 0.6 ? null : ({ point: new Vector3(at.x, at.y, 0), normal: up }));
  assert.equal(gap.blocked, true); assert.ok(gap.position.x < 0.4, 'interior gap is sampled even if final endpoint has floor');
  const steep = resolveWalkMovement(feet, new Vector3(0.1, 0, 0), 1, at => ({ point: at.clone(), normal: new Vector3(1, 0, 0.1) }));
  assert.equal(steep.blocked, true);
  assert.equal(isWalkableSurface(new Vector3(0, 0, -1)), true, 'opposite triangle winding is allowed');
  assert.equal(isWalkableSurface(new Vector3()), false);
});

test('walking obstacle checks stop partial travel and preserve unit-scaled limits', () => {
  for (const scale of [1, 1000, 1 / 0.3048]) {
    let samples = 0;
    const result = resolveWalkMovement(new Vector3(), new Vector3(0.4 * scale, 0, 0), scale,
      (at, rise, drop) => {
        samples++;
        near(rise / scale, 0.3); near(drop / scale, 0.5);
        return { point: new Vector3(at.x, at.y, 0), normal: new Vector3(0, 0, 1) };
      }, (_from, to) => to.x > 0.25 * scale);
    assert.equal(result.blocked, true); assert.ok(samples >= 3);
    assert.ok(result.position.x <= 0.25 * scale);
  }
  const excessive = resolveWalkMovement(new Vector3(), new Vector3(1e8, 0, 0), 1, () => { throw new Error('must not run'); });
  assert.equal(excessive.blocked, true);
});

class TestWindow extends EventTarget {
  private counter = 0;
  readonly frames = new Map<number, FrameRequestCallback>();
  now = 0;
  requestAnimationFrame(callback: FrameRequestCallback): number { const id = ++this.counter; this.frames.set(id, callback); return id; }
  cancelAnimationFrame(id: number): void { this.frames.delete(id); }
  advance(milliseconds = 50): void {
    this.now += milliseconds;
    const callbacks = [...this.frames.values()]; this.frames.clear();
    for (const callback of callbacks) callback(this.now);
  }
}

class TestDocument extends EventTarget {
  readonly defaultView = new TestWindow();
  activeElement: EventTarget | null = null;
  hidden = false;
}

class TestCanvas extends EventTarget {
  readonly ownerDocument = new TestDocument();
  private captures = new Set<number>();
  focus(): void { this.ownerDocument.activeElement = this; }
  setPointerCapture(id: number): void { this.captures.add(id); }
  hasPointerCapture(id: number): boolean { return this.captures.has(id); }
  releasePointerCapture(id: number): void {
    this.captures.delete(id);
    this.dispatchEvent(inputEvent('lostpointercapture', { pointerId: id }));
  }
}

function inputEvent(type: string, properties: Record<string, unknown> = {}): Event {
  const event = new Event(type, { cancelable: true });
  Object.assign(event, properties);
  return event;
}

function controllerFixture() {
  const canvas = new TestCanvas();
  const camera = new PerspectiveCamera(45, 1, 0.01, 1000); camera.up.set(0, 0, 1);
  camera.position.set(0, -10, 4); camera.lookAt(0, 0, 2);
  const orbit = { enabled: true, target: new Vector3(0, 0, 2), minDistance: 0.01, maxDistance: 100, update() {} };
  const states: NavigationState[] = [];
  const person = { feet: new Vector3(), scale: 0, yaw: 0, height: 0 };
  const controller = new NavigationControls({ canvas: canvas as unknown as HTMLCanvasElement, camera, orbit: orbit as unknown as OrbitControls,
    sampleGround: at => ({ point: new Vector3(at.x, at.y, 0), normal: new Vector3(0, 0, 1) }),
    blockedByObstacle: () => false, cameraObstruction: () => null,
    onWalkPosition: (feet, yaw, scale, height) => { person.feet.copy(feet); person.yaw = yaw; person.scale = scale; person.height = height; },
    onStateChange: state => states.push(state), onRender: () => {} });
  const click = (button = 0) => canvas.dispatchEvent(inputEvent('pointerdown', { isPrimary: true, button, pointerId: 1, clientX: 10, clientY: 10 }));
  const key = (code: string, extra: Record<string, unknown> = {}) => canvas.dispatchEvent(inputEvent('keydown', { code, ...extra }));
  const release = (code: string) => canvas.ownerDocument.defaultView.dispatchEvent(inputEvent('keyup', { code }));
  return { controller, camera, orbit, canvas, person, states, click, key, release, window: canvas.ownerDocument.defaultView, document: canvas.ownerDocument };
}

test('navigation requires a model, declared scale and explicit canvas activation before accepting keys', () => {
  const f = controllerFixture();
  assert.equal(f.controller.setMode('walk'), false);
  assert.equal(f.controller.getState().reason, 'no-model');
  f.controller.setModelAvailable(true);
  assert.equal(f.controller.setMode('fly'), false);
  assert.equal(f.controller.getState().reason, 'unknown-unit');
  f.controller.setUnit('m'); assert.equal(f.controller.setMode('fly'), true);
  const original = f.camera.position.clone();
  f.key('KeyW'); f.window.advance(); f.window.advance();
  assert.deepEqual(f.camera.position.toArray(), original.toArray());
  f.click(); f.key('KeyW');
  f.window.advance(); f.window.advance();
  assert.ok(f.camera.position.distanceTo(original) > 0.06);
  f.release('KeyW'); f.window.advance();
  assert.equal(f.window.frames.size, 0, 'idle navigation does not render continuously');
  f.controller.dispose();
});

test('third-person walking awaits a surface, keeps a real avatar and camera separate, and exits safely', () => {
  const f = controllerFixture();
  f.controller.setUnit('mm'); f.controller.setModelAvailable(true); f.controller.setMode('walk');
  assert.equal(f.controller.getState().needsGroundPlacement, true);
  assert.equal(f.controller.getState().avatarVisible, false);
  f.click(); f.key('KeyW'); f.window.advance();
  assert.equal(f.window.frames.size, 0, 'no movement before placement');
  assert.equal(f.controller.placeOnGround({ point: new Vector3(), normal: new Vector3(1, 0, 0) }), false);
  assert.equal(f.controller.placeOnGround({ point: new Vector3(), normal: new Vector3(0, 0, 1) }), true);
  assert.equal(f.controller.getState().avatarVisible, true);
  assert.equal(f.person.scale, 1000); assert.equal(f.person.height, 1.7);
  assert.ok(f.camera.position.distanceTo(f.person.feet) > 3000, 'third-person camera follows behind the person');
  const start = f.person.feet.clone();
  f.key('KeyW'); f.window.advance(); f.window.advance();
  near(f.person.feet.distanceTo(start), 70, 1e-6);
  f.key('Escape');
  assert.equal(f.controller.getState().mode, 'orbit');
  assert.equal(f.controller.getState().avatarVisible, false);
  assert.equal(f.orbit.enabled, true); assert.equal(f.window.frames.size, 0);
  assert.ok(f.orbit.maxDistance >= f.camera.position.distanceTo(f.orbit.target), 'returning from meter-scaled navigation cannot clamp a millimeter camera to old limits');
  f.controller.dispose();
});

test('right dragging changes the view without pointer lock and normal release keeps navigation active', () => {
  const f = controllerFixture();
  f.controller.setUnit('m'); f.controller.setModelAvailable(true); f.controller.setMode('fly');
  const direction = f.camera.getWorldDirection(new Vector3());
  f.click(2);
  f.canvas.dispatchEvent(inputEvent('pointermove', { pointerId: 1, clientX: 110, clientY: 30 }));
  assert.ok(f.camera.getWorldDirection(new Vector3()).distanceTo(direction) > 0.2);
  f.canvas.dispatchEvent(inputEvent('pointerup', { pointerId: 1 }));
  assert.equal(f.controller.getState().active, true);
  assert.equal(f.canvas.hasPointerCapture(1), false);
  f.key('KeyE'); f.window.advance(); f.window.advance();
  const height = f.camera.position.z;
  f.release('KeyE'); f.window.advance();
  f.key('KeyQ'); f.window.advance(); f.window.advance();
  assert.ok(f.camera.position.z < height);
  f.controller.dispose();
});

test('input focus, window blur and hidden tabs clear held keys and require a new canvas click', () => {
  for (const stop of ['focus', 'panel', 'blur', 'hidden', 'modifier', 'pointercancel'] as const) {
    const f = controllerFixture();
    f.controller.setUnit('m'); f.controller.setModelAvailable(true); f.controller.setMode('fly');
    f.click(); f.key('KeyW'); f.window.advance(); f.window.advance();
    if (stop === 'focus') { f.document.activeElement = new EventTarget(); f.document.dispatchEvent(new Event('focusin')); }
    if (stop === 'panel') f.document.dispatchEvent(new Event('pointerdown'));
    if (stop === 'blur') f.window.dispatchEvent(new Event('blur'));
    if (stop === 'hidden') { f.document.hidden = true; f.document.dispatchEvent(new Event('visibilitychange')); }
    if (stop === 'modifier') f.key('ControlLeft', { ctrlKey: true });
    if (stop === 'pointercancel') f.canvas.dispatchEvent(new Event('pointercancel'));
    const position = f.camera.position.clone();
    f.document.hidden = false; f.canvas.focus();
    f.key('KeyW'); f.window.advance(10000); f.window.advance();
    assert.equal(f.controller.getState().active, false, stop);
    assert.deepEqual(f.camera.position.toArray(), position.toArray(), stop);
    assert.equal(f.window.frames.size, 0, stop);
    f.controller.dispose();
  }
});

test('unit changes, placement reset, model removal and disposal release all input/frames', () => {
  const f = controllerFixture();
  f.controller.setUnit('m'); f.controller.setModelAvailable(true); f.controller.setMode('walk');
  f.click(); f.controller.placeOnGround({ point: new Vector3(), normal: new Vector3(0, 0, 1) });
  f.controller.resetWalkPlacement();
  assert.equal(f.controller.getState().needsGroundPlacement, true); assert.equal(f.controller.getState().avatarVisible, false);
  f.controller.setMode('fly'); f.click(); f.key('KeyW');
  f.controller.setUnit('ft');
  assert.equal(f.controller.getState().mode, 'orbit'); assert.equal(f.window.frames.size, 0);
  f.controller.setMode('fly'); f.controller.setModelAvailable(false);
  assert.equal(f.controller.getState().mode, 'orbit'); assert.equal(f.controller.getState().reason, 'no-model');
  assert.throws(() => f.controller.setSpeed(NaN)); assert.throws(() => f.controller.setSpeed(0));
  assert.throws(() => f.controller.setEyeHeight(20));
  f.controller.setModelAvailable(true); f.controller.setMode('fly'); f.click(); f.key('KeyW');
  f.controller.dispose(); const changes = f.states.length;
  f.click(); f.key('KeyW'); f.document.dispatchEvent(new Event('focusin')); f.window.advance();
  assert.equal(f.states.length, changes); assert.equal(f.window.frames.size, 0);
  assert.equal(f.controller.setMode('fly'), false);
});
