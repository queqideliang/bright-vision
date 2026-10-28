import { ModelArchive, decodeZipName, type ModelSource } from '../import/zip.ts';
import { jsonRecords } from '../import/json-stream.ts';
import { parseQuantitySnapshot } from '../quantities.ts';
import type {
  BimComponent, BimGeometry, BimLevel, BimMaterial, BimModel, BimNode,
  LoadProgress, PropertyGroups,
} from '../model.ts';

export interface ModelLimits {
  compressedBytes: number;
  uncompressedBytes: number;
  entryBytes: number;
  entries: number;
  components: number;
  records: number;
}

/** Hard ceilings are checked from the central directory before allocating ZIP output. */
export const MODEL_LIMITS: Readonly<ModelLimits> = Object.freeze({
  compressedBytes: 2_147_483_648,
  uncompressedBytes: 32 * 1024 * 1024 * 1024,
  entryBytes: 16 * 1024 * 1024 * 1024,
  entries: 1_000_000,
  components: 2_000_000,
  records: 8_000_000,
});

interface ZipEntry { name: string; originalName: string; originalSize: number; compressedSize: number; crc: number }
type ObjectValue = Record<string, unknown>;
const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const unsafeKeys = new Set(['__proto__', 'constructor', 'prototype']);
const own = (value: object, key: string) => Object.hasOwn(value, key);
const dict = <T>(): Record<string, T> => Object.create(null) as Record<string, T>;
function fail(message: string): never { throw new Error(message); }

function object(value: unknown, where: string): ObjectValue {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) fail(`${where} 应为 JSON 对象。`);
  return value as ObjectValue;
}

function text(value: unknown, fallback = ''): string {
  if (value === null || value === undefined) return fallback;
  if (typeof value !== 'string' && typeof value !== 'number') return fallback;
  return String(value).slice(0, 4096);
}

function id(value: unknown, where: string): string {
  if (typeof value !== 'string' && typeof value !== 'number') fail(`${where} 缺少有效标识。`);
  if (typeof value === 'number' && (!Number.isSafeInteger(value) || value < 0)) fail(`${where} 标识无效。`);
  const result = String(value);
  if (!result || result.length > 1024 || unsafeKeys.has(result)) fail(`${where} 标识无效。`);
  return result;
}

function finiteArray(value: unknown, where: string, length?: number): number[] {
  if (!Array.isArray(value) || (length !== undefined && value.length !== length)) fail(`${where} 数组长度无效。`);
  for (const n of value) {
    if (typeof n !== 'number' || !Number.isFinite(n)) fail(`${where} 包含非有限数字。`);
  }
  return value as number[];
}

function matrix(value: unknown, where: string): Float64Array {
  return new Float64Array(value === undefined ? identity : finiteArray(value, where, 16));
}

function normalizedName(name: string): string { return name.replaceAll('\\', '/'); }

function safeName(name: string): void {
  const parts = name.split('/');
  if (!name || name.startsWith('/') || /[\u0000-\u001f:]/u.test(name)
    || parts.some(p => p === '..' || p === '.' || unsafeKeys.has(p))) fail('ZIP 包含不安全的文件路径。');
}

/** Read only the ZIP directory; never extract entries onto the filesystem. ZIP64 is intentionally unsupported. */
export function inspectModelZip(bytes: Uint8Array, limits: Readonly<ModelLimits> = MODEL_LIMITS): ZipEntry[] {
  if (bytes.byteLength > limits.compressedBytes) fail('ZIP 超过本版本允许的压缩文件大小（2 GB）。');
  if (bytes.byteLength < 22) fail('文件不是有效的 ZIP 模型包。');
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let end = -1;
  const lower = Math.max(0, bytes.byteLength - 22 - 65_535);
  for (let i = bytes.byteLength - 22; i >= lower; i--) {
    if (view.getUint32(i, true) === 0x06054b50 && i + 22 + view.getUint16(i + 20, true) === bytes.byteLength) {
      end = i; break;
    }
  }
  if (end < 0) fail('ZIP 目录损坏，或使用了当前不支持的分卷格式。');
  const count = view.getUint16(end + 10, true);
  if (view.getUint16(end + 4, true) || view.getUint16(end + 6, true)
    || view.getUint16(end + 8, true) !== count) fail('暂不支持分卷 ZIP。');
  const directorySize = view.getUint32(end + 12, true);
  const directoryOffset = view.getUint32(end + 16, true);
  if (count === 0xffff || directorySize === 0xffffffff || directoryOffset === 0xffffffff) fail('暂不支持 ZIP64，请分批导出模型。');
  if (!count || count > limits.entries) fail('ZIP 文件数量超过限制，或包内没有文件。');
  if (directoryOffset + directorySize !== end) fail('ZIP 中央目录范围无效。');
  const entries: ZipEntry[] = [];
  const names = new Set<string>();
  let total = 0;
  let at = directoryOffset;
  for (let i = 0; i < count; i++) {
    if (at + 46 > end || view.getUint32(at, true) !== 0x02014b50) fail('ZIP 中央目录损坏。');
    const flags = view.getUint16(at + 8, true);
    const compression = view.getUint16(at + 10, true);
    const crc = view.getUint32(at + 16, true);
    const compressedSize = view.getUint32(at + 20, true);
    const originalSize = view.getUint32(at + 24, true);
    const nameSize = view.getUint16(at + 28, true);
    const extraSize = view.getUint16(at + 30, true);
    const commentSize = view.getUint16(at + 32, true);
    const localOffset = view.getUint32(at + 42, true);
    const next = at + 46 + nameSize + extraSize + commentSize;
    if (next > end) fail('ZIP 文件名或扩展区越界。');
    if (flags & 1) fail('暂不支持加密 ZIP，请使用未加密的本地导出包。');
    if (compression !== 0 && compression !== 8) fail('ZIP 使用了不支持的压缩方法。');
    if ([compressedSize, originalSize, localOffset].includes(0xffffffff)) fail('暂不支持 ZIP64。');
    const originalName = decodeZipName(bytes.subarray(at + 46, at + 46 + nameSize), flags,
      bytes.subarray(at + 46 + nameSize, at + 46 + nameSize + extraSize));
    const name = normalizedName(originalName!);
    safeName(name);
    const canonicalName = name.toLocaleLowerCase('en-US');
    if (names.has(canonicalName)) fail('ZIP 中存在重复文件名。');
    names.add(canonicalName);
    if (originalSize > limits.entryBytes) fail('ZIP 中单个文件超过解压大小限制。');
    total += originalSize;
    if (!Number.isSafeInteger(total) || total > limits.uncompressedBytes) fail('ZIP 解压后的总大小超过限制，请检查模型包。');
    if (localOffset + 30 > directoryOffset || view.getUint32(localOffset, true) !== 0x04034b50) fail('ZIP 本地文件头无效。');
    const localFlags = view.getUint16(localOffset + 6, true);
    const localCompression = view.getUint16(localOffset + 8, true);
    const localNameSize = view.getUint16(localOffset + 26, true);
    const localExtraSize = view.getUint16(localOffset + 28, true);
    const dataAt = localOffset + 30 + localNameSize + localExtraSize;
    if (localFlags !== flags || localCompression !== compression || dataAt + compressedSize > directoryOffset) fail('ZIP 本地文件记录与中央目录不一致。');
    if (!(flags & 8) && (view.getUint32(localOffset + 14, true) !== crc
      || view.getUint32(localOffset + 18, true) !== compressedSize
      || view.getUint32(localOffset + 22, true) !== originalSize)) fail('ZIP 本地文件校验记录与中央目录不一致。');
    const localName = bytes.subarray(localOffset + 30, localOffset + 30 + localNameSize);
    const centralName = bytes.subarray(at + 46, at + 46 + nameSize);
    if (localName.length !== centralName.length || localName.some((b, index) => b !== centralName[index])) fail('ZIP 文件名记录不一致。');
    entries.push({ name, originalName: originalName!, originalSize, compressedSize, crc });
    at = next;
  }
  if (at !== directoryOffset + directorySize) fail('ZIP 中央目录大小不一致。');
  return entries;
}

function propertyValue(value: unknown, depth = 0): unknown {
  if (depth > 32) fail('属性嵌套超过允许深度。');
  if (typeof value === 'number' && !Number.isFinite(value)) fail('属性包含非有限数字。');
  if (Array.isArray(value)) return value.map(v => propertyValue(v, depth + 1));
  if (value && typeof value === 'object') {
    const result = dict<unknown>();
    for (const [key, item] of Object.entries(value)) result[key] = propertyValue(item, depth + 1);
    return result;
  }
  return value;
}

function propertyGroups(value: unknown): PropertyGroups {
  const result = dict<Record<string, unknown>>();
  const ungrouped = dict<unknown>();
  for (const [key, item] of Object.entries(object(value, '属性记录'))) {
    if (item !== null && typeof item === 'object' && !Array.isArray(item)) result[key] = propertyValue(item) as Record<string, unknown>;
    else ungrouped[key] = propertyValue(item);
  }
  if (Object.keys(ungrouped).length) result['未分组字段'] = ungrouped;
  return result;
}

function samePropertyValue(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object') return false;
  if (Array.isArray(left) !== Array.isArray(right)) return false;
  const a = left as Record<string, unknown>; const b = right as Record<string, unknown>;
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every(key => own(b, key) && samePropertyValue(a[key], b[key]));
}

function geometry(value: unknown, geometryId: string): BimGeometry {
  const raw = object(value, '几何记录');
  if (raw.type !== 'MESH') fail('模型包含当前尚不支持的非 MESH 几何。');
  const positions = finiteArray(raw.positions, '几何顶点');
  if (positions.length % 3) fail('几何顶点数量不是三的倍数。');
  const floatPositions = new Float32Array(positions);
  if (floatPositions.some(n => !Number.isFinite(n))) fail('几何顶点超出支持的浮点范围。');
  const indices = finiteArray(raw.indices, '三角面索引');
  if (indices.length % 3) fail('三角面索引数量不是三的倍数。');
  for (const n of indices) {
    if (!Number.isSafeInteger(n) || n < 0 || n >= positions.length / 3 || n > 0xffffffff) fail('三角面索引超出顶点范围。');
  }
  const result: BimGeometry = { id: geometryId, positions: floatPositions, indices: new Uint32Array(indices) };
  for (const field of ['normals', 'uv'] as const) {
    if (raw[field] !== undefined) {
      const values = finiteArray(raw[field], `几何 ${field}`);
      if (values.length) {
        const expected = field === 'normals' ? positions.length : positions.length / 3 * 2;
        if (values.length !== expected) fail(`几何 ${field} 数量与顶点不匹配。`);
        const typed = new Float32Array(values);
        if (typed.some(n => !Number.isFinite(n))) fail(`几何 ${field} 超出支持的浮点范围。`);
        result[field] = typed;
      }
    }
  }
  return result;
}

/** Parse a local model archive incrementally without inflating the entire ZIP. */
export async function parseModelZip(
  bytes: ModelSource,
  name: string,
  onProgress?: (progress: LoadProgress) => void,
  limits: Readonly<ModelLimits> = MODEL_LIMITS,
  selectedPrefix?: string,
): Promise<BimModel> {
  const report = (phase: string, progress: number) => onProgress?.({ phase, progress });
  report('检查模型包', 0.02);
  const archive = new ModelArchive(bytes, limits);
  const entries = await archive.inspect();
  const componentEntries = entries.filter(e => /(^|\/)components\/components[^/]*\.json$/u.test(e.name));
  if (!componentEntries.length) {
    if (entries.some(e => /\.zip$/iu.test(e.name))) fail('这是包含多个模型 ZIP 的外层压缩包。请先解压外层，再选择内部的单个模型 ZIP。');
    if (entries.some(e => /\.(max|fbx)$/iu.test(e.name))) fail('这个 ZIP 中是 3ds Max／FBX 文件。本版尚未接入这些格式，请选择已导出的模型 JSON ZIP。');
    if (entries.some(e => /(?:^|\/)assets\/.*\.js$/iu.test(e.name)) && entries.some(e => /(?:^|\/)index\.html$/iu.test(e.name))) fail('这是查看器的网页部署包。请解压后部署网页，或选择 models 文件夹中的模型 ZIP。');
    fail('未找到构件数据。请选择包含构件、几何和属性的单模型 ZIP。');
  }
  const prefixes = new Set(componentEntries.map(e => e.name.slice(0, e.name.lastIndexOf('/components/') + 1)));
  // A root-level components file has no preceding slash.
  if (componentEntries.some(e => e.name.startsWith('components/'))) prefixes.add('');
  if (selectedPrefix === undefined && prefixes.size !== 1) fail('ZIP 中包含多个模型目录，请选择要打开的模型。');
  if (selectedPrefix !== undefined && !prefixes.has(selectedPrefix)) fail('选择的模型目录已失效，请重新打开文件。');
  const prefix = selectedPrefix ?? [...prefixes][0];
  const selectedComponents = componentEntries.filter(e => e.name.startsWith(prefix) && /^components\/components[^/]*\.json$/u.test(e.name.slice(prefix.length)));
  const isNeeded = (path: string) => {
    if (!path.startsWith(prefix)) return false;
    const relative = path.slice(prefix.length);
    return /^(components|geometries|nodes|materials|properties)\/[^/]+\.json$/u.test(relative)
      || relative === 'LevelInfo.json' || /^texture\/[^/]+\.(png|jpe?g|webp)$/iu.test(relative);
  };
  const geometries = dict<BimGeometry>();
  const nodes = dict<BimNode>();
  const materials = dict<BimMaterial>();
  const properties = dict<PropertyGroups>();
  const textures = dict<{ bytes: Uint8Array; mimeType: string }>();
  const components: BimComponent[] = [];
  const warnings: string[] = [];
  let units: BimModel['units'];
  const manifest = entries.find(e => e.name === `${prefix}manifest.json`);
  if (manifest) {
    for await (const [key, value] of jsonRecords(archive.chunks(manifest), false, 'manifest.json')) {
      if (key === 'linkedModels' && value === false) warnings.push('此模型包未包含链接模型，请单独导出并核对链接模型。');
      if (key === 'textureImages' && value === false) warnings.push('此模型包保留材质颜色，未包含材质贴图。');
      if (key === 'lengthUnit' && (value === 'm' || value === 'mm' || value === 'ft')) units = { length: value, source: '模型包 manifest.json' };
      if (key === 'warnings' && Array.isArray(value)) {
        for (const warning of value.slice(0, 100)) if (typeof warning === 'string' && warning.trim()) warnings.push(`导出提示：${warning.slice(0, 4096)}`);
      }
    }
  }
  const missing = new Map<string, number>();
  const countMissing = (kind: string) => missing.set(kind, (missing.get(kind) ?? 0) + 1);
  const jsonFiles = entries.filter(e => isNeeded(e.name) && e.name.endsWith('.json')
    && e.name !== `${prefix}LevelInfo.json` && !componentEntries.includes(e)).sort((a, b) => a.name.localeCompare(b.name));
  let processed = 0;
  let recordCount = 0;
  for (const entry of jsonFiles) {
    const relative = entry.name.slice(prefix.length);
    const group = relative.split('/')[0];
    for await (const [key, value] of jsonRecords(archive.chunks(entry), false, relative)) {
      if (++recordCount > limits.records) fail('模型记录数量超过本版本限制。');
      id(key, `${group} 记录`);
      if (group === 'geometries') {
        if (own(geometries, key)) fail('几何标识重复，无法可靠关联构件。');
        geometries[key] = geometry(value, key);
      } else if (group === 'nodes') {
        if (own(nodes, key)) fail('节点标识重复，无法可靠关联构件。');
        const raw = object(value, '节点记录');
        nodes[key] = { id: key, geometryId: id(raw.geometry, '节点几何'), materialId: id(raw.material, '节点材质'), matrix: matrix(raw.matrix, '节点矩阵') };
      } else if (group === 'properties') {
        const groups = propertyGroups(value);
        if (own(properties, key)) {
          if (!samePropertyValue(properties[key], groups)) fail(`属性标识 ${key} 在不同分块中的数据冲突，无法可靠关联构件。`);
        } else properties[key] = groups;
      } else if (group === 'materials') {
        materials[key] = { id: key, raw: propertyValue(object(value, '材质记录')) as ObjectValue };
      }
    }
    report('分段读取几何和属性', 0.08 + 0.62 * (++processed / Math.max(1, jsonFiles.length)));
  }
  for (const [nodeId, node] of Object.entries(nodes)) {
    if (!own(geometries, node.geometryId)) {
      countMissing('节点引用的几何');
      delete nodes[nodeId];
    } else if (!own(materials, node.materialId)) countMissing('节点引用的材质（使用默认材质）');
  }
  const componentById = new Map<string, BimComponent>();
  const resolveProperties = (reference: unknown, kind: string): PropertyGroups => {
    if (reference === undefined || reference === null || reference === '') return dict<Record<string, unknown>>();
    const key = id(reference, kind);
    if (!own(properties, key)) { countMissing(kind); return dict<Record<string, unknown>>(); }
    return properties[key];
  };
  for (const entry of selectedComponents) {
   for await (const [, value] of jsonRecords(archive.chunks(entry), true, entry.name)) {
    if (components.length >= limits.components) fail('模型构件数量超过本版本限制。');
    const raw = object(value, '构件记录');
    const componentId = id(raw.Id, '构件');
    if (componentById.has(componentId)) fail('构件标识重复，请分别打开不同模型。');
    if (!Array.isArray(raw.Nodes)) fail('构件缺少节点数组。');
    const nodeIds: string[] = [];
    for (const value of raw.Nodes) {
      const nodeId = id(value, '构件节点');
      if (own(nodes, nodeId)) nodeIds.push(nodeId); else countMissing('构件引用的节点');
    }
    const component: BimComponent = {
      id: componentId, name: text(raw.name, componentId), category: text(raw.type, text(raw.BuildInCategory, '未分类')),
      categoryCode: text(raw.BuildInCategory), family: text(raw.familyName), type: text(raw.familySymbol),
      matrix: matrix(raw.matrix, '构件矩阵'), nodeIds,
      properties: resolveProperties(raw.Property, '实例属性'),
      typeProperties: resolveProperties(raw.SymbolProperty, '类型属性'),
      familyProperties: resolveProperties(raw.FamilyProperty, '族属性'),
      levelNames: [],
    };
    const quantity = parseQuantitySnapshot(raw.QuantitySnapshot);
    if (quantity.snapshot) component.quantitySnapshot = quantity.snapshot;
    if (quantity.issue) component.quantityIssue = quantity.issue;
    if (raw.minBoundary !== undefined) component.min = finiteArray(raw.minBoundary, '构件包围盒下界', 3);
    if (raw.maxBoundary !== undefined) component.max = finiteArray(raw.maxBoundary, '构件包围盒上界', 3);
    components.push(component);
    componentById.set(componentId, component);
   }
  }
  report('读取楼层和纹理', 0.82);
  if (!components.length || !Object.keys(geometries).length || !Object.keys(nodes).length) fail('模型包没有可显示的构件几何。');
  const levels: BimLevel[] = [];
  const levelFile = entries.find(e => e.name === `${prefix}LevelInfo.json`);
  if (levelFile) {
    for await (const [levelName, value] of jsonRecords(archive.chunks(levelFile), false, 'LevelInfo.json')) {
      const raw = object(value, '楼层记录');
      const attribute = raw.attribute === undefined ? dict<unknown>() : object(raw.attribute, '楼层属性');
      const level: BimLevel = { name: levelName, componentIds: [] };
      if (typeof attribute.elevation === 'number' && Number.isFinite(attribute.elevation)) level.elevation = attribute.elevation;
      const levelElements = raw.elements === undefined ? dict<unknown>() : object(raw.elements, '楼层构件分类');
      const included = new Set<string>();
      for (const items of Object.values(levelElements)) {
        if (!Array.isArray(items)) fail('楼层分类构件应为数组。');
        for (const item of items) {
          const elementId = id(object(item, '楼层构件').id, '楼层构件');
          const component = componentById.get(elementId);
          if (!component) { countMissing('楼层中未包含在导出构件里的标识'); continue; }
          if (!included.has(elementId)) { included.add(elementId); level.componentIds.push(elementId); component.levelNames.push(levelName); }
        }
      }
      levels.push(level);
    }
  } else warnings.push('模型包没有 LevelInfo.json，暂不提供楼层分类。');
  for (const entry of entries) {
    const relative = entry.name.slice(prefix.length);
    if (isNeeded(entry.name) && relative.startsWith('texture/')) {
      if (entry.originalSize > 256 * 1024 * 1024) fail('单张纹理超过 256 MiB，请降低纹理分辨率。');
      const data = new Uint8Array(entry.originalSize); let offset = 0;
      for await (const chunk of archive.chunks(entry)) { data.set(chunk, offset); offset += chunk.length; }
      const extension = relative.split('.').pop()!.toLowerCase();
      textures[relative.slice('texture/'.length)] = { bytes: data, mimeType: extension === 'png' ? 'image/png' : extension === 'webp' ? 'image/webp' : 'image/jpeg' };
    }
  }
  for (const [kind, count] of missing) warnings.push(`${kind}：${count.toLocaleString('zh-CN')} 处未解析。相关信息可能不完整。`);
  const noGeometry = components.filter(c => !c.nodeIds.length).length;
  if (noGeometry) warnings.push(`${noGeometry.toLocaleString('zh-CN')} 个构件没有可显示几何，属性仍予以保留。`);
  let triangles = 0;
  for (const g of Object.values(geometries)) triangles += g.indices.length / 3;
  report('模型解析完成', 1);
  return {
    name: name.slice(0, 1024), units, components, geometries, nodes, materials, textures, levels, warnings,
    stats: { components: components.length, geometries: Object.keys(geometries).length, triangles, nodes: Object.keys(nodes).length },
  };
}
