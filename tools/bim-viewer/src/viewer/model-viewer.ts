import {
  AmbientLight, Box3, Box3Helper, BufferAttribute, BufferGeometry, Color,
  DataTexture, DirectionalLight, DoubleSide, GridHelper, Group, InstancedMesh, InstancedBufferAttribute,
  LinearFilter, LinearSRGBColorSpace, Matrix3, Matrix4, Mesh,
  MeshPhongMaterial, NearestFilter, PerspectiveCamera, Raycaster,
  RepeatWrapping, RGBAFormat, Scene, Sphere, SRGBColorSpace, Texture, Plane, Ray, Line, LineBasicMaterial, Points, PointsMaterial,
  TextureLoader, UnsignedByteType, Vector2, Vector3, WebGLRenderer,
} from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import type { BimComponent, BimGeometry, BimModel, BimNode } from '../model.ts';
import { geometryBounds, materialSignature, texturePathCandidates, textureTransform, vertexNormals } from './geometry.ts';
import { BoundsTree, TriangleIndex, canInstanceTransform, rayBoxDistance } from './spatial.ts';

export interface WorldBox { min: [number, number, number]; max: [number, number, number] }
export interface MeasurementResult { points: [number, number, number][]; distance: number | null }
export interface ViewerViewpoint {
  version: 1;
  position: [number, number, number];
  target: [number, number, number];
  visibleIds: string[];
  selectedIds: string[];
  ghostedIds?: string[];
  ghostMode: boolean;
  sectionBox: WorldBox | null;
}

interface Placement {
  componentIndex: number;
  component: BimComponent;
  node: BimNode;
  geometry: BimGeometry;
  position: Vector3;
}

interface Batch {
  mesh: Mesh<BufferGeometry, MeshPhongMaterial>;
  owners: Uint32Array;
  componentIndices: number[];
  ghostMesh?: Mesh<BufferGeometry, MeshPhongMaterial>;
  bounds: Box3;
  triangleIndex: TriangleIndex;
  instanceMatrices?: Matrix4[];
  instanceTree?: BoundsTree;
  instanceBounds?: Box3[];
}

const MAX_BATCH_VERTICES = 180_000;
const MAX_BATCH_INDICES = 900_000;
const IDENTITY_COLOR = new Color(0xb5c4d5);

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function coordinateTuple(value: unknown): value is [number, number, number] {
  return Array.isArray(value) && value.length === 3 && value.every(item => typeof item === 'number' && Number.isFinite(item));
}

function rawColor(value: unknown, fallback: Color): Color {
  if (typeof value === 'string' && /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(value)) return new Color(value);
  if (Array.isArray(value) && value.length >= 3 && value.slice(0, 3).every((item) => typeof item === 'number' && Number.isFinite(item))) {
    const divisor = value.slice(0, 3).some((item) => item > 1) ? 255 : 1;
    return new Color().setRGB(value[0] / divisor, value[1] / divisor, value[2] / divisor, SRGBColorSpace);
  }
  if (typeof value === 'number' && Number.isFinite(value)) return new Color(value);
  return fallback.clone();
}

function yieldFrame(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

/**
 * Shared browser/Electron renderer. Geometry is merged by appearance in bounded
 * chunks; a component-state texture preserves selection and visibility without
 * one draw call per Revit node. All model/texture resources remain on this device.
 */
export class ModelViewer {
  private readonly container: HTMLElement;
  private readonly onSelect: (id: string | null) => void;
  private readonly onProgress?: (phase: string, progress: number) => void;
  private readonly renderer: WebGLRenderer;
  private readonly scene = new Scene();
  private readonly camera = new PerspectiveCamera(45, 1, 0.1, 10_000);
  private readonly controls: OrbitControls;
  private readonly modelRoot = new Group();
  private readonly raycaster = new Raycaster();
  private readonly pointer = new Vector2();
  private readonly resizeObserver: ResizeObserver;
  private readonly selectionBox = new Box3Helper(new Box3(), 0x1688ff);
  private readonly sectionHelper = new Box3Helper(new Box3(), 0x0c997c);
  private readonly measurementRoot = new Group();
  private readonly materials = new Set<MeshPhongMaterial>();
  private readonly textures = new Set<Texture>();
  private readonly blobUrls = new Set<string>();
  private readonly textureCache = new Map<string, Promise<Texture | null>>();
  private readonly ghostMaterials = new Map<MeshPhongMaterial, MeshPhongMaterial>();
  private batches: Batch[] = [];
  private componentIds: string[] = [];
  private componentIndex = new Map<string, number>();
  private componentBounds: Box3[] = [];
  private componentHasGeometry: boolean[] = [];
  private stateTexture: DataTexture | null = null;
  private stateBytes = new Uint8Array();
  private stateWidth = 1;
  private stateHeight = 1;
  private selectedId: string | null = null;
  private selectedIds = new Set<string>();
  private ghostMode = false;
  private isolatedIds: Set<string> | null = null;
  private sectionBounds: Box3 | null = null;
  private sectionPlanes: Plane[] = [];
  private measurementMode = false;
  private measurementPoints: Vector3[] = [];
  private batchTree: BoundsTree | null = null;
  private lastPick = { batches: 0, triangles: 0, milliseconds: 0 };
  private instanceCount = 0;
  onSelectionChange?: (ids: string[]) => void;
  onMeasurementChange?: (measurement: MeasurementResult | null) => void;
  private center = new Vector3();
  private bounds = new Box3();
  private grid: GridHelper | null = null;
  private disposed = false;
  private loadVersion = 0;
  private requestedFrame = 0;
  private pointerStart: { x: number; y: number; id: number } | null = null;
  private unavailable = false;

  constructor(
    container: HTMLElement,
    onSelect: (id: string | null) => void,
    onProgress?: (phase: string, progress: number) => void,
  ) {
    this.container = container;
    this.onSelect = onSelect;
    this.onProgress = onProgress;
    try {
      this.renderer = new WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance', logarithmicDepthBuffer: true });
    } catch {
      throw new Error('无法创建三维视图。请启用浏览器硬件加速，或更新显卡驱动后重试。');
    }
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.outputColorSpace = SRGBColorSpace;
    this.renderer.localClippingEnabled = true;
    this.renderer.setClearColor(0xeff3f8, 1);
    this.renderer.domElement.className = 'model-canvas';
    this.renderer.domElement.setAttribute('aria-label', '三维模型，拖动旋转，滚轮缩放，单击选择构件');
    this.renderer.domElement.tabIndex = 0;
    this.container.append(this.renderer.domElement);
    this.camera.up.set(0, 0, 1);
    this.camera.position.set(10, -14, 10);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = false;
    this.controls.screenSpacePanning = true;
    this.controls.addEventListener('change', this.requestRender);
    this.scene.add(new AmbientLight(0xffffff, 1.4));
    const key = new DirectionalLight(0xffffff, 2.3);
    key.position.set(1, -2, 3);
    this.scene.add(key);
    const fill = new DirectionalLight(0xe0ebff, 1.4);
    fill.position.set(-2, 1, 1);
    this.scene.add(fill);
    this.scene.add(this.modelRoot);
    this.selectionBox.visible = false;
    const selectionMaterials = Array.isArray(this.selectionBox.material) ? this.selectionBox.material : [this.selectionBox.material];
    for (const material of selectionMaterials) {
      material.depthTest = false;
      material.transparent = true;
      material.opacity = 0.95;
    }
    this.selectionBox.renderOrder = 10;
    this.scene.add(this.selectionBox);
    this.sectionHelper.visible = false;
    this.scene.add(this.sectionHelper);
    this.scene.add(this.measurementRoot);
    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    const canvas = this.renderer.domElement;
    canvas.addEventListener('pointerdown', this.handlePointerDown);
    canvas.addEventListener('pointerup', this.handlePointerUp);
    canvas.addEventListener('pointercancel', this.handlePointerCancel);
    canvas.addEventListener('webglcontextlost', this.handleContextLost);
    canvas.addEventListener('webglcontextrestored', this.handleContextRestored);
    this.resize();
  }

  async load(model: BimModel): Promise<void> {
    if (this.disposed) throw new Error('查看器已关闭。');
    const version = ++this.loadVersion;
    this.clearModel();
    try {
    this.componentIds = model.components.map((component) => component.id);
    this.componentIndex = new Map(this.componentIds.map((id, index) => [id, index]));
    this.componentBounds = model.components.map(() => new Box3());
    this.componentHasGeometry = model.components.map(() => false);
    const geometryBoxes = new Map<string, Box3>();
    const buckets = new Map<string, { raw: Record<string, unknown>; placements: Placement[] }>();
    const signatures = new Map<string, string>();
    const fallbackMaterial: Record<string, unknown> = {};
    const matrix = new Matrix4();
    const localNode = new Matrix4();
    const transformedBounds = new Box3();
    let placements = 0;
    let skippedNodes = 0;
    let lastYield = performance.now();
    this.onProgress?.('准备模型场景', 0);
    for (let componentIndex = 0; componentIndex < model.components.length; componentIndex++) {
      const component = model.components[componentIndex]!;
      for (const nodeId of component.nodeIds) {
        const node = model.nodes[nodeId];
        const geometry = node && model.geometries[node.geometryId];
        if (!node || !geometry || geometry.positions.length === 0 || geometry.indices.length === 0) {
          skippedNodes++;
          continue;
        }
        let box = geometryBoxes.get(geometry.id);
        if (!box) {
          box = geometryBounds(geometry);
          geometryBoxes.set(geometry.id, box);
        }
        matrix.fromArray(component.matrix).multiply(localNode.fromArray(node.matrix));
        transformedBounds.copy(box).applyMatrix4(matrix);
        this.componentBounds[componentIndex]!.union(transformedBounds);
        this.bounds.union(transformedBounds);
        this.componentHasGeometry[componentIndex] = true;
        const raw = model.materials[node.materialId]?.raw ?? fallbackMaterial;
        let signature = signatures.get(node.materialId);
        if (!signature) {
          signature = materialSignature(raw);
          signatures.set(node.materialId, signature);
        }
        let bucket = buckets.get(signature);
        if (!bucket) {
          bucket = { raw, placements: [] };
          buckets.set(signature, bucket);
        }
        bucket.placements.push({ componentIndex, component, node, geometry, position: transformedBounds.getCenter(new Vector3()) });
        placements++;
      }
      if (performance.now() - lastYield > 14) {
        this.onProgress?.('准备模型场景', componentIndex / Math.max(1, model.components.length) * 0.25);
        await yieldFrame();
        this.assertCurrent(version);
        lastYield = performance.now();
      }
    }
    geometryBoxes.clear();
    if (this.bounds.isEmpty()) {
      this.clearModel();
      throw new Error('模型中没有可以显示的有效三角面。');
    }
    if (skippedNodes > 0) model.warnings.push(`显示时跳过 ${skippedNodes} 个缺失或无有效三角面的节点。`);
    this.center = this.bounds.getCenter(new Vector3());
    for (const box of this.componentBounds) if (!box.isEmpty()) box.translate(this.center.clone().negate());
    this.bounds.translate(this.center.clone().negate());
    this.createStateTexture(model.components.length);
    const normals = new Map<string, Float32Array>();
    const imageLookup = new Map<string, { bytes: Uint8Array; mimeType: string }>();
    for (const [path, image] of Object.entries(model.textures)) {
      imageLookup.set(path.replaceAll('\\', '/').toLowerCase(), image);
      const basename = path.replaceAll('\\', '/').split('/').at(-1);
      if (basename && !imageLookup.has(basename.toLowerCase())) imageLookup.set(basename.toLowerCase(), image);
    }
    let rendered = 0;
    const sharedGeometry = new Map<string, { geometry: BufferGeometry; index: TriangleIndex }>();
    for (const bucket of buckets.values()) {
      this.assertCurrent(version);
      const material = await this.createMaterial(bucket.raw, imageLookup, model, version);
      this.assertCurrent(version);
      const divisions = Math.min(8, Math.max(1, Math.ceil(Math.cbrt(placements / 512))));
      const cellSize = Math.max(this.bounds.getSize(new Vector3()).length() / divisions, 0.001);
      const regions = new Map<string, Placement[]>();
      for (const placement of bucket.placements) {
        const p = placement.position;
        const key = bucket.placements.length < 64 ? 'all' : [p.x - this.center.x - this.bounds.min.x, p.y - this.center.y - this.bounds.min.y, p.z - this.center.z - this.bounds.min.z]
          .map(value => Math.floor(value / cellSize)).join(':');
        let region = regions.get(key);
        if (!region) { region = []; regions.set(key, region); }
        region.push(placement);
      }
      for (const region of regions.values()) {
      const repeated = new Map<string, Placement[]>();
      for (const placement of region) {
        matrix.fromArray(placement.component.matrix).multiply(localNode.fromArray(placement.node.matrix));
        if (!canInstanceTransform(matrix.elements)) continue;
        let group = repeated.get(placement.geometry.id);
        if (!group) { group = []; repeated.set(placement.geometry.id, group); }
        group.push(placement);
      }
      const instanced = new Set<Placement>();
      for (const group of repeated.values()) {
        if (group.length < 4) continue;
        const batch = this.buildInstancedBatch(group, material, normals, sharedGeometry);
        this.addBatch(batch);
        group.forEach(placement => instanced.add(placement));
        rendered += group.length;
        this.onProgress?.('构建实例与空间索引', 0.25 + rendered / Math.max(1, placements) * 0.7);
        this.requestRender();
        await yieldFrame();
        this.assertCurrent(version);
      }
      const merged = region.filter(placement => !instanced.has(placement));
      let start = 0;
      while (start < merged.length) {
        let end = start;
        let vertices = 0;
        let indices = 0;
        while (end < merged.length) {
          const placement = merged[end]!;
          const addedVertices = placement.geometry.positions.length / 3;
          const addedIndices = placement.geometry.indices.length;
          if (end > start && (vertices + addedVertices > MAX_BATCH_VERTICES || indices + addedIndices > MAX_BATCH_INDICES)) break;
          vertices += addedVertices;
          indices += addedIndices;
          end++;
        }
        const batch = this.buildBatch(merged, start, end, vertices, indices, material, normals);
        this.addBatch(batch);
        rendered += end - start;
        this.onProgress?.('构建三维视图', 0.25 + rendered / Math.max(1, placements) * 0.7);
        this.requestRender();
        await yieldFrame();
        this.assertCurrent(version);
        start = end;
      }
      }
      bucket.placements.length = 0;
    }
    this.batchTree = BoundsTree.fromBoxes(this.batches.map(batch => batch.bounds), 4);
    sharedGeometry.clear();
    normals.clear();
    // All placements now live in render batches; release the imported CPU copy.
    model.geometries = Object.create(null);
    model.nodes = Object.create(null);
    model.textures = Object.create(null);
    imageLookup.clear();
    this.addGrid();
    this.focus();
    this.onProgress?.('模型已就绪', 1);
    } catch (error) {
      if (!this.disposed && version === this.loadVersion) this.clearModel();
      throw error;
    }
  }

  private buildBatch(
    placements: Placement[], start: number, end: number, vertexCount: number, indexCount: number,
    material: MeshPhongMaterial, normalCache: Map<string, Float32Array>,
  ): Batch {
    const positions = new Float32Array(vertexCount * 3);
    const normals = new Float32Array(vertexCount * 3);
    const uv = new Float32Array(vertexCount * 2);
    const componentIndices = new Float32Array(vertexCount);
    const indices = new Uint32Array(indexCount);
    const owners = new Uint32Array(indexCount / 3);
    const batchOwners = new Set<number>();
    const matrix = new Matrix4();
    const nodeMatrix = new Matrix4();
    const normalMatrix = new Matrix3();
    const point = new Vector3();
    const normal = new Vector3();
    let vertexOffset = 0;
    let indexOffset = 0;
    for (let i = start; i < end; i++) {
      const placement = placements[i]!;
      const geometry = placement.geometry;
      const count = geometry.positions.length / 3;
      matrix.fromArray(placement.component.matrix).multiply(nodeMatrix.fromArray(placement.node.matrix));
      matrix.elements[12]! -= this.center.x;
      matrix.elements[13]! -= this.center.y;
      matrix.elements[14]! -= this.center.z;
      normalMatrix.getNormalMatrix(matrix);
      let sourceNormals = normalCache.get(geometry.id);
      if (!sourceNormals) {
        sourceNormals = vertexNormals(geometry);
        normalCache.set(geometry.id, sourceNormals);
      }
      for (let vertex = 0; vertex < count; vertex++) {
        const source = vertex * 3;
        const target = (vertexOffset + vertex) * 3;
        point.fromArray(geometry.positions, source).applyMatrix4(matrix).toArray(positions, target);
        normal.fromArray(sourceNormals, source).applyMatrix3(normalMatrix).normalize().toArray(normals, target);
        if (geometry.uv?.length === count * 2) {
          uv[(vertexOffset + vertex) * 2] = geometry.uv[vertex * 2]!;
          uv[(vertexOffset + vertex) * 2 + 1] = geometry.uv[vertex * 2 + 1]!;
        }
        componentIndices[vertexOffset + vertex] = placement.componentIndex;
      }
      const mirrored = matrix.determinant() < 0;
      for (let index = 0; index < geometry.indices.length; index += 3) {
        indices[indexOffset + index] = geometry.indices[index]! + vertexOffset;
        indices[indexOffset + index + 1] = geometry.indices[index + (mirrored ? 2 : 1)]! + vertexOffset;
        indices[indexOffset + index + 2] = geometry.indices[index + (mirrored ? 1 : 2)]! + vertexOffset;
        owners[(indexOffset + index) / 3] = placement.componentIndex;
      }
      vertexOffset += count;
      indexOffset += geometry.indices.length;
      batchOwners.add(placement.componentIndex);
    }
    const geometry = new BufferGeometry();
    geometry.setAttribute('position', new BufferAttribute(positions, 3));
    geometry.setAttribute('normal', new BufferAttribute(normals, 3));
    geometry.setAttribute('uv', new BufferAttribute(uv, 2));
    geometry.setAttribute('bimComponentIndex', new BufferAttribute(componentIndices, 1));
    geometry.setIndex(new BufferAttribute(indices, 1));
    geometry.computeBoundingBox();
    geometry.computeBoundingSphere();
    const mesh = new Mesh(geometry, material);
    mesh.name = 'BIM geometry batch';
    const batch: Batch = { mesh, owners, componentIndices: [...batchOwners], bounds: geometry.boundingBox!.clone(), triangleIndex: new TriangleIndex(geometry) };
    mesh.userData.bimBatch = batch;
    return batch;
  }

  private buildInstancedBatch(
    placements: Placement[], material: MeshPhongMaterial, normalCache: Map<string, Float32Array>,
    shared: Map<string, { geometry: BufferGeometry; index: TriangleIndex }>,
  ): Batch {
    const source = placements[0]!.geometry;
    let base = shared.get(source.id);
    if (!base) {
      const geometry = new BufferGeometry();
      let normals = normalCache.get(source.id);
      if (!normals) { normals = vertexNormals(source); normalCache.set(source.id, normals); }
      geometry.setAttribute('position', new BufferAttribute(source.positions, 3));
      geometry.setAttribute('normal', new BufferAttribute(normals, 3));
      geometry.setAttribute('uv', new BufferAttribute(source.uv?.length === source.positions.length / 3 * 2 ? source.uv : new Float32Array(source.positions.length / 3 * 2), 2));
      geometry.setIndex(new BufferAttribute(source.indices, 1));
      geometry.computeBoundingBox();
      geometry.computeBoundingSphere();
      base = { geometry, index: new TriangleIndex(geometry) };
      shared.set(source.id, base);
    }
    const geometry = new BufferGeometry();
    for (const [key, attribute] of Object.entries(base.geometry.attributes)) geometry.setAttribute(key, attribute);
    geometry.setIndex(base.geometry.index);
    geometry.boundingBox = base.geometry.boundingBox!.clone();
    geometry.boundingSphere = base.geometry.boundingSphere!.clone();
    const owners = Uint32Array.from(placements, placement => placement.componentIndex);
    geometry.setAttribute('bimComponentIndex', new InstancedBufferAttribute(Float32Array.from(owners), 1));
    const mesh = new InstancedMesh(geometry, material, placements.length);
    const instanceMatrices: Matrix4[] = [];
    const instanceBounds: Box3[] = [];
    const local = new Matrix4();
    const bounds = new Box3();
    for (let i = 0; i < placements.length; i++) {
      const placement = placements[i]!;
      const matrix = new Matrix4().fromArray(placement.component.matrix).multiply(local.fromArray(placement.node.matrix));
      matrix.elements[12]! -= this.center.x;
      matrix.elements[13]! -= this.center.y;
      matrix.elements[14]! -= this.center.z;
      mesh.setMatrixAt(i, matrix);
      instanceMatrices.push(matrix);
      const box = geometry.boundingBox.clone().applyMatrix4(matrix);
      instanceBounds.push(box);
      bounds.union(box);
    }
    mesh.instanceMatrix.needsUpdate = true;
    mesh.computeBoundingBox();
    mesh.computeBoundingSphere();
    mesh.name = 'BIM shared geometry instances';
    this.instanceCount += placements.length;
    const batch: Batch = { mesh, owners, componentIndices: [...new Set(owners)], bounds,
      triangleIndex: base.index, instanceMatrices, instanceBounds, instanceTree: BoundsTree.fromBoxes(instanceBounds, 8) };
    mesh.userData.bimBatch = batch;
    return batch;
  }

  private addBatch(batch: Batch): void {
    let material = this.ghostMaterials.get(batch.mesh.material);
    if (!material) {
      material = batch.mesh.material.clone();
      material.transparent = true;
      material.depthWrite = false;
      material.opacity = 0.12;
      this.configureStateMaterial(material, true);
      this.ghostMaterials.set(batch.mesh.material, material);
      this.materials.add(material);
    }
    let ghost: Mesh<BufferGeometry, MeshPhongMaterial>;
    if (batch.mesh instanceof InstancedMesh) {
      const mesh = new InstancedMesh(batch.mesh.geometry, material, batch.mesh.count);
      mesh.instanceMatrix = batch.mesh.instanceMatrix;
      mesh.boundingBox = batch.mesh.boundingBox?.clone() ?? null;
      mesh.boundingSphere = batch.mesh.boundingSphere?.clone() ?? null;
      ghost = mesh;
    } else ghost = new Mesh(batch.mesh.geometry, material);
    ghost.name = 'BIM ghost context';
    ghost.visible = false;
    ghost.renderOrder = 1;
    batch.ghostMesh = ghost;
    this.modelRoot.add(batch.mesh, ghost);
    this.batches.push(batch);
  }

  private createStateTexture(count: number): void {
    const maxSize = this.renderer.capabilities.maxTextureSize;
    this.stateWidth = Math.max(1, Math.min(count, maxSize, 4096));
    this.stateHeight = Math.max(1, Math.ceil(count / this.stateWidth));
    if (this.stateHeight > maxSize) throw new Error('构件数量超过当前显卡可支持的范围。');
    this.stateBytes = new Uint8Array(this.stateWidth * this.stateHeight * 4);
    for (let index = 0; index < count; index++) {
      this.stateBytes[index * 4] = 255;
      this.stateBytes[index * 4 + 3] = 255;
    }
    this.stateTexture = new DataTexture(this.stateBytes, this.stateWidth, this.stateHeight, RGBAFormat, UnsignedByteType);
    this.stateTexture.minFilter = NearestFilter;
    this.stateTexture.magFilter = NearestFilter;
    this.stateTexture.generateMipmaps = false;
    this.stateTexture.needsUpdate = true;
  }

  private async createMaterial(
    raw: Record<string, unknown>, images: Map<string, { bytes: Uint8Array; mimeType: string }>,
    model: BimModel, version: number,
  ): Promise<MeshPhongMaterial> {
    const opacity = Math.min(1, Math.max(0, finiteNumber(raw.opacity, 1)));
    const material = new MeshPhongMaterial({
      color: rawColor(raw.color, IDENTITY_COLOR),
      specular: rawColor(raw.specular, new Color(0x252b32)),
      emissive: rawColor(raw.emissive, new Color(0)),
      emissiveIntensity: Math.max(0, finiteNumber(raw.emissiveIntensity, 1)),
      shininess: Math.max(0, Math.min(200, finiteNumber(raw.shininess, 24))),
      opacity, transparent: opacity < 1, depthWrite: opacity >= 1, side: DoubleSide,
    });
    this.materials.add(material);
    for (const [key, colorTexture] of [['map', true], ['bumpMap', false]] as const) {
      const path = raw[key];
      if (typeof path !== 'string' || !path) continue;
      const imageKey = texturePathCandidates(path).find((candidate) => images.has(candidate));
      if (!imageKey) {
        model.warnings.push(`有材质引用的本地贴图未包含在模型包中。`);
        continue;
      }
      let texturePromise = this.textureCache.get(`${key}:${imageKey}:${JSON.stringify([
        raw[`${key}ScaleX`], raw[`${key}ScaleY`], raw[`${key}OffsetX`], raw[`${key}OffsetY`], raw[`${key}WAngle`],
      ])}`);
      if (!texturePromise) {
        const image = images.get(imageKey)!;
        texturePromise = this.loadTexture(image, colorTexture, raw, key, version);
        this.textureCache.set(`${key}:${imageKey}:${JSON.stringify([
          raw[`${key}ScaleX`], raw[`${key}ScaleY`], raw[`${key}OffsetX`], raw[`${key}OffsetY`], raw[`${key}WAngle`],
        ])}`, texturePromise);
      }
      const texture = await texturePromise;
      this.assertCurrent(version);
      if (texture) {
        material[key] = texture;
        if (key === 'bumpMap') material.bumpScale = finiteNumber(raw.bumpScale, 1);
      } else model.warnings.push('有本地贴图无法解码，已使用材质颜色显示。');
    }
    this.configureStateMaterial(material, false);
    return material;
  }

  private configureStateMaterial(material: MeshPhongMaterial, ghost: boolean): void {
    const stateTexture = this.stateTexture;
    const stateSize = new Vector2(this.stateWidth, this.stateHeight);
    material.onBeforeCompile = (shader) => {
      shader.uniforms.bimStateMap = { value: stateTexture };
      shader.uniforms.bimStateSize = { value: stateSize };
      shader.vertexShader = `attribute float bimComponentIndex;\nvarying float vBimComponentIndex;\n${shader.vertexShader}`
        .replace('#include <begin_vertex>', '#include <begin_vertex>\nvBimComponentIndex = bimComponentIndex;');
      shader.fragmentShader = `uniform sampler2D bimStateMap;\nuniform vec2 bimStateSize;\nvarying float vBimComponentIndex;\n${shader.fragmentShader}`
        .replace('void main() {', `void main() {\nfloat bimIndex = floor(vBimComponentIndex + 0.5);\nvec2 bimUV = (vec2(mod(bimIndex, bimStateSize.x), floor(bimIndex / bimStateSize.x)) + 0.5) / bimStateSize;\nvec4 bimState = texture2D(bimStateMap, bimUV);\nif (${ghost ? 'bimState.r < 0.25 || bimState.r > 0.75' : 'bimState.r < 0.75'}) discard;`)
        .replace('#include <color_fragment>', '#include <color_fragment>\ndiffuseColor.rgb = mix(diffuseColor.rgb, vec3(0.03, 0.42, 1.0), bimState.g * 0.75);');
    };
    material.clippingPlanes = this.sectionPlanes;
    material.customProgramCacheKey = () => `bim-component-state-v3-${ghost ? 'ghost' : 'solid'}`;
  }

  private async loadTexture(
    image: { bytes: Uint8Array; mimeType: string }, colorTexture: boolean,
    raw: Record<string, unknown>, prefix: string, version: number,
  ): Promise<Texture | null> {
    const url = URL.createObjectURL(new Blob([image.bytes.slice().buffer], { type: image.mimeType }));
    this.blobUrls.add(url);
    try {
      const texture = await new TextureLoader().loadAsync(url);
      if (this.disposed || version !== this.loadVersion) {
        texture.dispose();
        return null;
      }
      texture.colorSpace = colorTexture ? SRGBColorSpace : LinearSRGBColorSpace;
      texture.wrapS = RepeatWrapping;
      texture.wrapT = RepeatWrapping;
      const transform = textureTransform(raw, prefix);
      texture.repeat.set(...transform.repeat);
      texture.offset.set(...transform.offset);
      texture.rotation = transform.rotation;
      texture.magFilter = LinearFilter;
      this.textures.add(texture);
      return texture;
    } catch {
      return null;
    }
  }

  private addGrid(): void {
    const size = this.bounds.getSize(new Vector3());
    const span = Math.max(size.x, size.y, size.z, 1);
    this.grid = new GridHelper(span * 1.5, 24, 0xc9d3e0, 0xdde4ed);
    this.grid.rotation.x = Math.PI / 2;
    this.grid.position.z = this.bounds.min.z - span * 0.005;
    this.scene.add(this.grid);
  }

  select(id: string | null): void {
    this.selectMany(id === null ? [] : [id]);
  }

  selectMany(ids: Iterable<string>, mode: 'replace' | 'add' | 'toggle' = 'replace'): void {
    const next = mode === 'replace' ? new Set<string>() : new Set(this.selectedIds);
    for (const id of ids) {
      const index = this.componentIndex.get(id);
      if (index === undefined || this.stateBytes[index * 4] === 0) continue;
      if (mode === 'toggle' && next.has(id)) next.delete(id);
      else { next.add(id); this.stateBytes[index * 4] = 255; }
    }
    const changed = next.size !== this.selectedIds.size || [...next].some(id => !this.selectedIds.has(id));
    if (!changed) return;
    for (const id of this.selectedIds) {
      const index = this.componentIndex.get(id);
      if (index !== undefined) this.stateBytes[index * 4 + 1] = 0;
    }
    this.selectedIds = next;
    this.selectedId = [...next].at(-1) ?? null;
    const box = new Box3();
    for (const id of next) {
      const index = this.componentIndex.get(id)!;
      this.stateBytes[index * 4 + 1] = 255;
      box.union(this.componentBounds[index]!);
    }
    this.selectionBox.visible = !box.isEmpty();
    if (this.selectionBox.visible) this.selectionBox.box.copy(box);
    for (const batch of this.batches) {
      batch.mesh.visible = batch.componentIndices.some(index => this.isVisible(index));
      if (batch.ghostMesh) batch.ghostMesh.visible = batch.componentIndices.some(index => this.stateBytes[index * 4] === 128);
    }
    if (this.stateTexture) this.stateTexture.needsUpdate = true;
    this.requestRender();
    this.onSelect(this.selectedId);
    this.onSelectionChange?.([...this.selectedIds]);
  }

  getSelectedIds(): string[] { return [...this.selectedIds]; }

  focusSelection(): void {
    const box = new Box3();
    for (const id of this.selectedIds) box.union(this.componentBounds[this.componentIndex.get(id)!]!);
    if (!box.isEmpty()) this.frameBox(box);
  }

  focus(id?: string): void {
    if (this.bounds.isEmpty()) return;
    const index = id === undefined ? undefined : this.componentIndex.get(id);
    const box = index === undefined ? this.visibleBounds() : this.componentBounds[index]!;
    if (box.isEmpty()) return;
    this.frameBox(box);
  }

  private frameBox(box: Box3): void {
    const sphere = box.getBoundingSphere(new Sphere());
    const radius = Math.max(sphere.radius, 0.001);
    const verticalHalf = this.camera.fov * Math.PI / 360;
    const horizontalHalf = Math.atan(Math.tan(verticalHalf) * this.camera.aspect);
    const distance = radius / Math.sin(Math.min(verticalHalf, horizontalHalf)) * 1.12;
    const direction = this.camera.position.clone().sub(this.controls.target).normalize();
    if (direction.lengthSq() === 0) direction.set(1, -1.4, 1).normalize();
    this.controls.target.copy(sphere.center);
    this.camera.position.copy(sphere.center).addScaledVector(direction, distance);
    this.camera.near = Math.max(radius / 100_000, 0.0001);
    this.camera.far = Math.max(distance * 100, this.bounds.getSize(new Vector3()).length() * 100, 100);
    this.controls.minDistance = Math.max(radius / 100_000, 0.0001);
    this.controls.maxDistance = Math.max(distance * 50, 100);
    this.camera.updateProjectionMatrix();
    this.controls.update();
    this.requestRender();
  }

  setVisible(ids: Iterable<string>, visible: boolean): void {
    for (const id of ids) {
      const index = this.componentIndex.get(id);
      if (index !== undefined) this.stateBytes[index * 4] = visible ? 255 : 0;
    }
    this.updateVisibility();
  }

  isolate(ids: Iterable<string>): void {
    const included = new Set(ids);
    this.isolatedIds = included;
    for (let index = 0; index < this.componentIds.length; index++) {
      this.stateBytes[index * 4] = included.has(this.componentIds[index]!) ? 255 : this.ghostMode ? 128 : 0;
    }
    this.updateVisibility();
  }

  showAll(): void {
    this.isolatedIds = null;
    for (let index = 0; index < this.componentIds.length; index++) this.stateBytes[index * 4] = 255;
    this.updateVisibility();
  }

  setGhostMode(enabled: boolean): void {
    if (this.ghostMode === enabled) return;
    this.ghostMode = enabled;
    for (let index = 0; index < this.componentIds.length; index++) {
      if (this.isolatedIds && !this.isolatedIds.has(this.componentIds[index]!)) this.stateBytes[index * 4] = enabled ? 128 : 0;
      else if (this.stateBytes[index * 4] === 128 && !enabled) this.stateBytes[index * 4] = 0;
    }
    this.updateVisibility();
  }

  private updateVisibility(): void {
    for (const batch of this.batches) {
      batch.mesh.visible = batch.componentIndices.some((index) => this.isVisible(index));
      if (batch.ghostMesh) batch.ghostMesh.visible = batch.componentIndices.some(index => this.stateBytes[index * 4] === 128);
    }
    if ([...this.selectedIds].some(id => !this.isVisible(this.componentIndex.get(id)!)))
      this.selectMany([...this.selectedIds].filter(id => this.isVisible(this.componentIndex.get(id)!)));
    if (this.stateTexture) this.stateTexture.needsUpdate = true;
    this.requestRender();
  }

  private isVisible(index: number): boolean {
    return this.stateBytes[index * 4] === 255;
  }

  private visibleBounds(): Box3 {
    const bounds = new Box3();
    for (let index = 0; index < this.componentBounds.length; index++) {
      if (this.isVisible(index)) bounds.union(this.componentBounds[index]!);
    }
    return bounds;
  }

  getHiddenCount(): number {
    return this.componentIds.reduce((count, _id, index) => count + (this.isVisible(index) ? 0 : 1), 0);
  }

  getVisibleIds(): string[] {
    return this.componentIds.filter((_id, index) => this.isVisible(index));
  }

  isComponentVisible(id: string): boolean {
    const index = this.componentIndex.get(id);
    return index !== undefined && this.isVisible(index);
  }

  getSelectedId(): string | null {
    return this.selectedId;
  }

  getComponentBounds(id: string): WorldBox | null {
    const index = this.componentIndex.get(id);
    const box = index === undefined ? undefined : this.componentBounds[index];
    if (!box || box.isEmpty()) return null;
    return { min: box.min.clone().add(this.center).toArray() as WorldBox['min'], max: box.max.clone().add(this.center).toArray() as WorldBox['max'] };
  }

  setSectionBox(box: WorldBox | null): void {
    if (box !== null && (!box || !coordinateTuple(box.min) || !coordinateTuple(box.max) || box.min.some((value, axis) => value > box.max[axis]!)))
      throw new Error('剖切盒坐标无效。');
    this.sectionBounds = box ? new Box3(new Vector3(...box.min).sub(this.center), new Vector3(...box.max).sub(this.center)) : null;
    this.sectionPlanes = this.sectionBounds ? [
      new Plane(new Vector3(1, 0, 0), -this.sectionBounds.min.x), new Plane(new Vector3(-1, 0, 0), this.sectionBounds.max.x),
      new Plane(new Vector3(0, 1, 0), -this.sectionBounds.min.y), new Plane(new Vector3(0, -1, 0), this.sectionBounds.max.y),
      new Plane(new Vector3(0, 0, 1), -this.sectionBounds.min.z), new Plane(new Vector3(0, 0, -1), this.sectionBounds.max.z),
    ] : [];
    for (const material of this.materials) { material.clippingPlanes = this.sectionPlanes; material.needsUpdate = true; }
    this.sectionHelper.visible = this.sectionBounds !== null;
    if (this.sectionBounds) this.sectionHelper.box.copy(this.sectionBounds);
    this.requestRender();
  }

  getSectionBox(): WorldBox | null {
    return this.sectionBounds ? { min: this.sectionBounds.min.clone().add(this.center).toArray() as WorldBox['min'], max: this.sectionBounds.max.clone().add(this.center).toArray() as WorldBox['max'] } : null;
  }

  sectionSelection(padding = 0): void {
    const box = new Box3();
    for (const id of this.selectedIds) box.union(this.componentBounds[this.componentIndex.get(id)!]!);
    if (box.isEmpty()) return;
    box.expandByScalar(Math.max(0, padding || box.getSize(new Vector3()).length() * 0.01));
    this.setSectionBox({ min: box.min.add(this.center).toArray() as WorldBox['min'], max: box.max.add(this.center).toArray() as WorldBox['max'] });
  }

  sectionAll(): void {
    if (this.bounds.isEmpty()) return;
    this.setSectionBox({ min: this.bounds.min.clone().add(this.center).toArray() as WorldBox['min'], max: this.bounds.max.clone().add(this.center).toArray() as WorldBox['max'] });
  }

  setMeasurementMode(enabled: boolean): void {
    this.measurementMode = enabled;
    this.renderer.domElement.style.cursor = enabled ? 'crosshair' : '';
  }

  clearMeasurement(): void {
    this.measurementPoints = [];
    this.clearMeasurementVisuals();
    this.onMeasurementChange?.(null);
    this.requestRender();
  }

  getMeasurement(): MeasurementResult | null {
    return this.measurementPoints.length ? { points: this.measurementPoints.map(point => point.clone().add(this.center).toArray() as [number, number, number]),
      distance: this.measurementPoints.length === 2 ? this.measurementPoints[0]!.distanceTo(this.measurementPoints[1]!) : null } : null;
  }

  private clearMeasurementVisuals(): void {
    for (const object of this.measurementRoot.children) {
      if (object instanceof Points || object instanceof Line) {
        object.geometry.dispose();
        if (Array.isArray(object.material)) object.material.forEach(material => material.dispose());
        else object.material.dispose();
      }
    }
    this.measurementRoot.clear();
  }

  private addMeasurement(point: Vector3): void {
    if (this.measurementPoints.length === 2) this.measurementPoints = [];
    this.measurementPoints.push(point.clone());
    this.clearMeasurementVisuals();
    const geometry = new BufferGeometry().setFromPoints(this.measurementPoints);
    this.measurementRoot.add(new Points(geometry, new PointsMaterial({ color: 0xe66012, size: 9, sizeAttenuation: false, depthTest: false })));
    if (this.measurementPoints.length === 2) this.measurementRoot.add(new Line(geometry.clone(), new LineBasicMaterial({ color: 0xe66012, depthTest: false })));
    this.measurementRoot.renderOrder = 20;
    this.onMeasurementChange?.(this.getMeasurement());
    this.requestRender();
  }

  captureView(): ViewerViewpoint {
    return { version: 1, position: this.camera.position.clone().add(this.center).toArray() as ViewerViewpoint['position'],
      target: this.controls.target.clone().add(this.center).toArray() as ViewerViewpoint['target'],
      visibleIds: this.getVisibleIds(), selectedIds: this.getSelectedIds(),
      ghostedIds: this.componentIds.filter((_id, index) => this.stateBytes[index * 4] === 128),
      ghostMode: this.ghostMode, sectionBox: this.getSectionBox() };
  }

  restoreView(view: ViewerViewpoint): void {
    const idList = (value: unknown): value is string[] => Array.isArray(value) && value.every(id => typeof id === 'string');
    const distanceSquared = view && coordinateTuple(view.position) && coordinateTuple(view.target) ? new Vector3(...view.position).distanceToSquared(new Vector3(...view.target)) : NaN;
    if (!view || view.version !== 1 || !coordinateTuple(view.position) || !coordinateTuple(view.target) || typeof view.ghostMode !== 'boolean'
      || !idList(view.visibleIds) || !idList(view.selectedIds) || (view.ghostedIds !== undefined && !idList(view.ghostedIds))
      || !Number.isFinite(distanceSquared) || distanceSquared < 1e-18)
      throw new Error('视点数据无效或版本不受支持。');
    this.setSectionBox(view.sectionBox);
    this.ghostMode = !!view.ghostMode;
    const visible = new Set(view.visibleIds);
    const ghosted = new Set(view.ghostedIds ?? []);
    for (let index = 0; index < this.componentIds.length; index++) this.stateBytes[index * 4] = visible.has(this.componentIds[index]!) ? 255 : ghosted.has(this.componentIds[index]!) ? 128 : 0;
    this.isolatedIds = ghosted.size ? visible : null;
    this.updateVisibility();
    this.selectMany(view.selectedIds);
    this.camera.position.fromArray(view.position).sub(this.center);
    this.controls.target.fromArray(view.target).sub(this.center);
    const distance = this.camera.position.distanceTo(this.controls.target);
    this.controls.minDistance = Math.max(this.bounds.getSize(new Vector3()).length() / 1_000_000, 0.0001);
    this.controls.maxDistance = Math.max(distance * 50, this.bounds.getSize(new Vector3()).length() * 100, 100);
    this.camera.far = Math.max(this.bounds.getSize(new Vector3()).length() * 100, distance * 100, 100);
    this.camera.updateProjectionMatrix();
    this.controls.update();
    this.requestRender();
  }

  getStats(): { drawCalls: number; triangles: number; renderedComponents: number; ghostedComponents: number; batches: number; instancedPlacements: number; renderBufferBytes: number; spatialIndexBytesEstimate: number; lastPickBatches: number; lastPickTriangles: number; lastPickMilliseconds: number } {
    const attributes = new Set<BufferAttribute | InstancedBufferAttribute>();
    const trees = new Set<TriangleIndex>();
    let spatialBytes = this.batchTree?.byteLength ?? 0;
    for (const batch of this.batches) {
      for (const attribute of Object.values(batch.mesh.geometry.attributes)) if (attribute instanceof BufferAttribute) attributes.add(attribute);
      if (batch.mesh.geometry.index) attributes.add(batch.mesh.geometry.index);
      if (batch.mesh instanceof InstancedMesh) attributes.add(batch.mesh.instanceMatrix);
      trees.add(batch.triangleIndex);
      spatialBytes += batch.instanceTree?.byteLength ?? 0;
    }
    for (const tree of trees) spatialBytes += tree.byteLength;
    return {
      drawCalls: this.renderer.info.render.calls,
      triangles: this.renderer.info.render.triangles,
      renderedComponents: this.componentIds.reduce((count, _id, index) => count + (this.componentHasGeometry[index] && this.isVisible(index) ? 1 : 0), 0),
      ghostedComponents: this.componentIds.reduce((count, _id, index) => count + (this.componentHasGeometry[index] && this.stateBytes[index * 4] === 128 ? 1 : 0), 0),
      batches: this.batches.length, instancedPlacements: this.instanceCount,
      renderBufferBytes: [...attributes].reduce((bytes, attribute) => bytes + attribute.array.byteLength, 0) + this.stateBytes.byteLength,
      spatialIndexBytesEstimate: spatialBytes,
      lastPickBatches: this.lastPick.batches, lastPickTriangles: this.lastPick.triangles, lastPickMilliseconds: this.lastPick.milliseconds,
    };
  }

  getResourceStats(): { geometries: number; textures: number; programs: number } {
    return {
      geometries: this.renderer.info.memory.geometries,
      textures: this.renderer.info.memory.textures,
      programs: this.renderer.info.programs?.length ?? 0,
    };
  }

  resize(): void {
    if (this.disposed) return;
    const width = Math.max(1, this.container.clientWidth);
    const height = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.requestRender();
  }

  private readonly requestRender = (): void => {
    if (this.requestedFrame || this.disposed || this.unavailable) return;
    this.requestedFrame = requestAnimationFrame(() => {
      this.requestedFrame = 0;
      if (!this.disposed && !this.unavailable) this.renderer.render(this.scene, this.camera);
    });
  };

  private readonly handlePointerDown = (event: PointerEvent): void => {
    if (event.button === 0 && event.isPrimary) this.pointerStart = { x: event.clientX, y: event.clientY, id: event.pointerId };
  };

  private readonly handlePointerCancel = (): void => { this.pointerStart = null; };

  private pointKept(point: Vector3): boolean {
    if (!this.sectionBounds) return true;
    const epsilon = Math.max(this.bounds.getSize(new Vector3()).length() * 1e-8, 1e-9);
    return this.sectionPlanes.every(plane => plane.distanceToPoint(point) >= -epsilon);
  }

  private pickRay(ray: Ray): { id: string; point: Vector3 } | null {
    const started = performance.now();
    const candidates = (this.batchTree?.queryRay(ray) ?? []).map(index => ({ batch: this.batches[index]!, distance: rayBoxDistance(ray, this.batches[index]!.bounds) }))
      .filter(candidate => candidate.batch.mesh.visible && Number.isFinite(candidate.distance)).sort((a, b) => a.distance - b.distance);
    let nearest = Infinity;
    let result: { id: string; point: Vector3 } | null = null;
    let batches = 0, triangles = 0;
    for (const { batch, distance } of candidates) {
      if (distance > nearest) break;
      batches++;
      if (batch.instanceMatrices && batch.instanceTree && batch.instanceBounds) {
        const instances = batch.instanceTree.queryRay(ray).map(index => ({ index, distance: rayBoxDistance(ray, batch.instanceBounds![index]!) }))
          .filter(instance => this.isVisible(batch.owners[instance.index]!) && instance.distance <= nearest).sort((a, b) => a.distance - b.distance);
        for (const instance of instances) {
          if (instance.distance > nearest) break;
          const matrix = batch.instanceMatrices[instance.index]!;
          const localRay = ray.clone().applyMatrix4(matrix.clone().invert());
          const point = new Vector3();
          const hit = batch.triangleIndex.raycast(localRay, (_triangle, local) => this.pointKept(point.copy(local).applyMatrix4(matrix)));
          triangles += batch.triangleIndex.lastTriangleTests;
          if (!hit) continue;
          point.copy(hit.point).applyMatrix4(matrix);
          const distance = point.distanceTo(ray.origin);
          if (distance < nearest) { nearest = distance; result = { id: this.componentIds[batch.owners[instance.index]!]!, point: point.clone() }; }
        }
      } else {
        const hit = batch.triangleIndex.raycast(ray, (triangle, point) => this.isVisible(batch.owners[triangle]!) && this.pointKept(point), nearest);
        triangles += batch.triangleIndex.lastTriangleTests;
        if (hit) { nearest = hit.distance; result = { id: this.componentIds[batch.owners[hit.triangle]!]!, point: hit.point }; }
      }
    }
    this.lastPick = { batches, triangles, milliseconds: performance.now() - started };
    return result;
  }

  private readonly handlePointerUp = (event: PointerEvent): void => {
    const start = this.pointerStart;
    this.pointerStart = null;
    if (!start || start.id !== event.pointerId || event.button !== 0 || Math.hypot(event.clientX - start.x, event.clientY - start.y) > 4) return;
    const rect = this.renderer.domElement.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    this.pointer.set((event.clientX - rect.left) / rect.width * 2 - 1, -(event.clientY - rect.top) / rect.height * 2 + 1);
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const hit = this.pickRay(this.raycaster.ray);
    if (this.measurementMode) { if (hit) this.addMeasurement(hit.point); return; }
    if (hit) this.selectMany([hit.id], event.ctrlKey || event.metaKey ? 'toggle' : 'replace');
    else if (!event.ctrlKey && !event.metaKey) this.select(null);
  };

  private readonly handleContextLost = (event: Event): void => {
    event.preventDefault();
    this.unavailable = true;
    this.onProgress?.('显卡上下文已丢失，等待恢复；如画面未恢复请重新打开模型。', 1);
  };

  private readonly handleContextRestored = (): void => {
    this.unavailable = false;
    this.requestRender();
  };

  private assertCurrent(version: number): void {
    if (this.disposed || version !== this.loadVersion) throw new DOMException('模型加载已取消。', 'AbortError');
  }

  private clearModel(): void {
    if (this.selectedId !== null) this.onSelect(null);
    if (this.selectedIds.size) this.onSelectionChange?.([]);
    this.selectedId = null;
    this.selectedIds.clear();
    this.selectionBox.visible = false;
    this.sectionHelper.visible = false;
    this.sectionBounds = null;
    this.sectionPlanes = [];
    this.ghostMode = false;
    this.isolatedIds = null;
    this.measurementMode = false;
    this.renderer.domElement.style.cursor = '';
    this.clearMeasurement();
    for (const batch of this.batches) {
      if (batch.mesh instanceof InstancedMesh) batch.mesh.dispose();
      if (batch.ghostMesh instanceof InstancedMesh) batch.ghostMesh.dispose();
      batch.mesh.geometry.dispose();
      batch.mesh.userData = {};
      if (batch.ghostMesh) batch.ghostMesh.userData = {};
    }
    this.batches = [];
    this.batchTree = null;
    this.instanceCount = 0;
    this.lastPick = { batches: 0, triangles: 0, milliseconds: 0 };
    this.modelRoot.clear();
    for (const material of this.materials) material.dispose();
    for (const texture of this.textures) texture.dispose();
    for (const url of this.blobUrls) URL.revokeObjectURL(url);
    this.materials.clear();
    this.textures.clear();
    this.blobUrls.clear();
    this.textureCache.clear();
    this.ghostMaterials.clear();
    this.stateTexture?.dispose();
    this.stateTexture = null;
    this.stateBytes = new Uint8Array();
    this.componentIds = [];
    this.componentIndex.clear();
    this.componentBounds = [];
    this.componentHasGeometry = [];
    this.bounds.makeEmpty();
    if (this.grid) {
      this.scene.remove(this.grid);
      this.grid.geometry.dispose();
      if (Array.isArray(this.grid.material)) for (const material of this.grid.material) material.dispose();
      else this.grid.material.dispose();
      this.grid = null;
    }
    this.renderer.renderLists.dispose();
    this.requestRender();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.loadVersion++;
    cancelAnimationFrame(this.requestedFrame);
    this.requestedFrame = 0;
    this.resizeObserver.disconnect();
    this.controls.removeEventListener('change', this.requestRender);
    this.controls.dispose();
    const canvas = this.renderer.domElement;
    canvas.removeEventListener('pointerdown', this.handlePointerDown);
    canvas.removeEventListener('pointerup', this.handlePointerUp);
    canvas.removeEventListener('pointercancel', this.handlePointerCancel);
    canvas.removeEventListener('webglcontextlost', this.handleContextLost);
    canvas.removeEventListener('webglcontextrestored', this.handleContextRestored);
    this.clearModel();
    this.selectionBox.geometry.dispose();
    if (Array.isArray(this.selectionBox.material)) for (const material of this.selectionBox.material) material.dispose();
    else this.selectionBox.material.dispose();
    this.sectionHelper.geometry.dispose();
    if (Array.isArray(this.sectionHelper.material)) this.sectionHelper.material.forEach(material => material.dispose());
    else this.sectionHelper.material.dispose();
    this.scene.clear();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
    canvas.remove();
  }
}
