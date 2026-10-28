import { MODEL_LIMITS, parseModelZip } from './import/parse.ts';
import type { BimModel } from './model.ts';
import type { SliceSource } from './import/zip.ts';
import { inspectPackage, unpackZipMember, type ArchiveChoice } from './import/packages.ts';
import type { ModelArchive } from './import/zip.ts';

// Bounded pages avoid a model-sized contiguous allocation and millions of IPC handles.
function transferBuffers(model: BimModel): Transferable[] {
  const buffers = new Set<ArrayBuffer>();
  const pool = <T extends Float32Array | Float64Array | Uint32Array>(create: (size: number) => T) => {
    let page = create(0); let used = 0;
    return (source: T): T => {
      const capacity = Math.floor(16 * 1024 * 1024 / source.BYTES_PER_ELEMENT);
      if (source.length > capacity) { buffers.add(source.buffer as ArrayBuffer); return source; }
      if (used + source.length > page.length) { page = create(capacity); used = 0; buffers.add(page.buffer as ArrayBuffer); }
      page.set(source, used); const view = page.subarray(used, used + source.length) as T;
      used += source.length; return view;
    };
  };
  const floats = pool<Float32Array>(size => new Float32Array(size));
  const indices = pool<Uint32Array>(size => new Uint32Array(size));
  const matrices = pool<Float64Array>(size => new Float64Array(size));
  for (const g of Object.values(model.geometries)) {
    g.positions = floats(g.positions); g.indices = indices(g.indices);
    if (g.normals) g.normals = floats(g.normals);
    if (g.uv) g.uv = floats(g.uv);
  }
  for (const item of model.components) item.matrix = matrices(item.matrix);
  for (const item of Object.values(model.nodes)) item.matrix = matrices(item.matrix);
  for (const texture of Object.values(model.textures)) buffers.add(texture.bytes.buffer as ArrayBuffer);
  return [...buffers];
}

let current: { source: SliceSource; name: string; archive: ModelArchive; choices: ArchiveChoice[]; internal: boolean; depth: number } | null = null;
const cleanups: (() => Promise<void>)[] = [];
async function openArchive(source: SliceSource, name: string, internal: boolean, depth = 0, prefix?: string): Promise<void> {
    const limits = internal ? { ...MODEL_LIMITS, compressedBytes: 32 * 1024 * 1024 * 1024 } : MODEL_LIMITS;
    if (prefix === undefined) {
      const { archive, choices } = await inspectPackage(source, limits);
      if (choices.length) {
        current = { source, name, archive, choices, internal, depth };
        self.postMessage({ type: 'choices', choices }); return;
      }
    }
    const model = await parseModelZip(source, name,
      progress => self.postMessage({ type: 'progress', ...progress }), limits, prefix);
    const transfer = transferBuffers(model);
    for (const cleanup of cleanups.splice(0)) await cleanup().catch(() => {});
    self.postMessage({ type: 'loaded', model }, { transfer });
}
self.onmessage = async (event: MessageEvent<{ file?: File | { url: string; size: number }; name?: string; type?: 'choose'; index?: number }>) => {
  try {
    if (event.data.type === 'choose') {
      if (!current) throw new Error('模型列表已失效，请重新打开压缩包。');
      const choice = current.choices.find(c => c.index === event.data.index);
      if (!choice) throw new Error('请选择有效的模型。');
      if (choice.kind === 'directory') { await openArchive(current.source, choice.name, current.internal, current.depth, choice.prefix); return; }
      if (choice.kind === 'rvt') throw new Error('原始 RVT 尚未支持独立读取，请选择已导出的模型 ZIP。');
      if (current.depth >= 4) throw new Error('ZIP 嵌套超过四层，请先解压外层。');
      self.postMessage({ type: 'progress', phase: '读取选中的模型', progress: 0.02 });
      const unpacked = await unpackZipMember(current.archive, choice.index); cleanups.push(unpacked.cleanup);
      await openArchive(unpacked.file, choice.name.split('/').at(-1)!, current.internal, current.depth + 1); return;
    }
    const file = event.data.file;
    if (!file) throw new Error('未选择模型文件。');
    const source: SliceSource = 'url' in file ? {
      size: file.size,
      slice: (start, end) => ({ arrayBuffer: async () => {
        const response = await fetch(file.url, { headers: { Range: `bytes=${start}-${end - 1}` } });
        if (response.status !== 206) throw new Error('无法分段读取转换后的模型。');
        const bytes = await response.arrayBuffer();
        if (bytes.byteLength !== end - start) throw new Error('模型文件读取不完整。');
        return bytes;
      } }),
    } : file;
    await openArchive(source, event.data.name ?? '模型.zip', 'url' in file);
  } catch (error) {
    for (const cleanup of cleanups.splice(0)) await cleanup().catch(() => {});
    self.postMessage({ type: 'error', message: error instanceof Error ? error.message : '模型读取失败' });
  }
};
