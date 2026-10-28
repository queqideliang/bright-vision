import { ModelArchive, type ModelSource } from './zip.ts';
import { MODEL_LIMITS, type ModelLimits } from './parse.ts';

export interface ArchiveChoice { index: number; name: string; size: number; kind: 'zip' | 'rvt' | 'directory'; prefix?: string }
export async function inspectPackage(source: ModelSource, limits: Readonly<ModelLimits> = MODEL_LIMITS): Promise<{ archive: ModelArchive; choices: ArchiveChoice[] }> {
  const archive = new ModelArchive(source, limits);
  const entries = await archive.inspect();
  const prefixes = new Set(entries.filter(e => /(^|\/)components\/components[^/]*\.json$/u.test(e.name))
    .map(e => e.name.startsWith('components/') ? '' : e.name.slice(0, e.name.lastIndexOf('/components/') + 1)));
  if (prefixes.size === 1) return { archive, choices: [] };
  if (prefixes.size > 1) return { archive, choices: [...prefixes].map((prefix, index) => ({ index, kind: 'directory', prefix, size: 0, name: prefix.replace(/\/$/u, '') || '模型' })) };
  const choices: ArchiveChoice[] = [];
  entries.forEach((entry, index) => {
    if (/\.(zip|rvt)$/iu.test(entry.name) && !entry.name.startsWith('__MACOSX/')) choices.push({ index, name: entry.name, size: entry.originalSize, kind: /\.rvt$/iu.test(entry.name) ? 'rvt' : 'zip' });
  });
  return { archive, choices };
}

/** Materialize only the chosen member. OPFS provides a disk-backed File for large nested archives. */
export async function unpackZipMember(archive: ModelArchive, index: number): Promise<{ file: File; cleanup: () => Promise<void> }> {
  const entry = archive.entries[index];
  if (!entry || !/\.zip$/iu.test(entry.name)) throw new Error('请选择包内的模型 ZIP。');
  let root: FileSystemDirectoryHandle | undefined;
  let name = '';
  try {
    root = await navigator.storage.getDirectory(); name = `bim-import-${crypto.randomUUID()}.zip`;
  } catch { /* Browsers without local temporary storage can still open smaller bundles. */ }
  if (root) {
    const fileHandle = await root.getFileHandle(name, { create: true });
    const output = await fileHandle.createWritable();
    try {
      for await (const chunk of archive.chunks(entry)) await output.write(chunk as Uint8Array<ArrayBuffer>);
      await output.close();
      return { file: await fileHandle.getFile(), cleanup: () => root!.removeEntry(name) };
    } catch (error) { await output.abort().catch(() => {}); await root.removeEntry(name).catch(() => {}); throw error; }
  }
  const chunks: BlobPart[] = [];
  for await (const chunk of archive.chunks(entry)) chunks.push(chunk.slice() as Uint8Array<ArrayBuffer>);
  return { file: new File(chunks, entry.name.split('/').at(-1)!, { type: 'application/zip' }), cleanup: async () => {} };
}
