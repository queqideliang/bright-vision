import { Inflate } from 'fflate';

export interface ArchiveLimits {
  compressedBytes: number; uncompressedBytes: number; entryBytes: number; entries: number;
}
export interface ZipEntry {
  name: string; originalName: string; originalSize: number; compressedSize: number;
  crc: number; offset: number; flags: number; compression: number; nameBytes: Uint8Array;
}
export interface SliceSource { size: number; slice(start: number, end: number): { arrayBuffer(): Promise<ArrayBuffer> } }
export type ModelSource = SliceSource | Uint8Array;
const decoder = new TextDecoder('utf-8', { fatal: true });
const crcTable = Uint32Array.from({ length: 256 }, (_, value) => {
  for (let i = 0; i < 8; i++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
  return value >>> 0;
});
function fail(message: string): never { throw new Error(message); }
function checksum(bytes: Uint8Array): number {
  let value = 0xffffffff;
  for (const byte of bytes) value = crcTable[(value ^ byte) & 255] ^ (value >>> 8);
  return (value ^ 0xffffffff) >>> 0;
}
/** UTF-8 flag, checked Unicode-path extension, then common legacy Windows encodings. */
export function decodeZipName(bytes: Uint8Array, flags: number, extra: Uint8Array = new Uint8Array()): string {
  if (flags & 0x800) {
    try { return decoder.decode(bytes); } catch { fail('ZIP 文件名的 UTF-8 编码损坏。'); }
  }
  const fields = new DataView(extra.buffer, extra.byteOffset, extra.byteLength);
  for (let at = 0; at + 4 <= extra.length;) {
    const size = fields.getUint16(at + 2, true); const next = at + 4 + size;
    if (next > extra.length) fail('ZIP 文件名扩展记录越界。');
    if (fields.getUint16(at, true) === 0x7075 && size >= 5 && extra[at + 4] === 1 && fields.getUint32(at + 5, true) === checksum(bytes)) {
      try { return decoder.decode(extra.subarray(at + 9, next)); } catch { /* fall back to the original name */ }
    }
    at = next;
  }
  try { return decoder.decode(bytes); } catch { /* unflagged legacy ZIP */ }
  try { return new TextDecoder('gb18030', { fatal: true }).decode(bytes); } catch { /* single-byte legacy ZIP */ }
  const cp437 = '\u00c7\u00fc\u00e9\u00e2\u00e4\u00e0\u00e5\u00e7\u00ea\u00eb\u00e8\u00ef\u00ee\u00ec\u00c4\u00c5\u00c9\u00e6\u00c6\u00f4\u00f6\u00f2\u00fb\u00f9\u00ff\u00d6\u00dc\u00a2\u00a3\u00a5\u20a7\u0192\u00e1\u00ed\u00f3\u00fa\u00f1\u00d1\u00aa\u00ba\u00bf\u2310\u00ac\u00bd\u00bc\u00a1\u00ab\u00bb\u2591\u2592\u2593\u2502\u2524\u2561\u2562\u2556\u2555\u2563\u2551\u2557\u255d\u255c\u255b\u2510\u2514\u2534\u252c\u251c\u2500\u253c\u255e\u255f\u255a\u2554\u2569\u2566\u2560\u2550\u256c\u2567\u2568\u2564\u2565\u2559\u2558\u2552\u2553\u256b\u256a\u2518\u250c\u2588\u2584\u258c\u2590\u2580\u03b1\u00df\u0393\u03c0\u03a3\u03c3\u00b5\u03c4\u03a6\u0398\u03a9\u03b4\u221e\u03c6\u03b5\u2229\u2261\u00b1\u2265\u2264\u2320\u2321\u00f7\u2248\u00b0\u2219\u00b7\u221a\u207f\u00b2\u25a0\u00a0';
  return Array.from(bytes, byte => byte < 128 ? String.fromCharCode(byte) : cp437[byte - 128]).join('');
}
function u64(view: DataView, offset: number): number {
  if (offset + 8 > view.byteLength) fail('ZIP64 扩展记录不完整。');
  const value = Number(view.getBigUint64(offset, true));
  if (!Number.isSafeInteger(value)) fail('ZIP64 数值超出支持范围。');
  return value;
}
export class ModelArchive {
  readonly size: number;
  entries: ZipEntry[] = [];
  private directoryOffset = 0;
  private source: ModelSource;
  private limits: Readonly<ArchiveLimits>;
  constructor(source: ModelSource, limits: Readonly<ArchiveLimits>) {
    this.source = source; this.limits = limits;
    this.size = source instanceof Uint8Array ? source.byteLength : source.size;
  }
  private async read(offset: number, length: number): Promise<Uint8Array> {
    if (offset < 0 || length < 0 || offset + length > this.size) fail('ZIP 文件记录越界。');
    return this.source instanceof Uint8Array ? this.source.subarray(offset, offset + length)
      : new Uint8Array(await this.source.slice(offset, offset + length).arrayBuffer());
  }
  async inspect(): Promise<ZipEntry[]> {
    if (this.size > this.limits.compressedBytes) fail('模型超过允许的压缩文件大小（2 GB）。');
    if (this.size < 22) fail('文件不是有效的 ZIP 模型包。');
    const tailStart = Math.max(0, this.size - 65_557);
    const tail = await this.read(tailStart, this.size - tailStart);
    const view = new DataView(tail.buffer, tail.byteOffset, tail.byteLength);
    let end = -1;
    for (let at = tail.length - 22; at >= 0; at--) {
      if (view.getUint32(at, true) === 0x06054b50 && at + 22 + view.getUint16(at + 20, true) === tail.length) { end = at; break; }
    }
    if (end < 0) fail('ZIP 目录损坏，或使用了不支持的分卷格式。');
    let count = view.getUint16(end + 10, true);
    let directorySize = view.getUint32(end + 12, true);
    let directoryOffset = view.getUint32(end + 16, true);
    if (view.getUint16(end + 4, true) || view.getUint16(end + 6, true) || view.getUint16(end + 8, true) !== count) fail('暂不支持分卷 ZIP。');
    let directoryEnd = tailStart + end;
    if (count === 0xffff || directorySize === 0xffffffff || directoryOffset === 0xffffffff) {
      const locator = await this.read(directoryEnd - 20, 20);
      const lv = new DataView(locator.buffer, locator.byteOffset, locator.length);
      if (lv.getUint32(0, true) !== 0x07064b50 || lv.getUint32(4, true) !== 0 || lv.getUint32(16, true) !== 1) fail('ZIP64 定位记录无效。');
      directoryEnd = u64(lv, 8);
      const record = await this.read(directoryEnd, 56);
      const rv = new DataView(record.buffer, record.byteOffset, record.length);
      if (rv.getUint32(0, true) !== 0x06064b50 || u64(rv, 4) < 44 || rv.getUint32(16, true) || rv.getUint32(20, true)
        || u64(rv, 24) !== u64(rv, 32)) fail('ZIP64 中央目录记录无效。');
      count = u64(rv, 32); directorySize = u64(rv, 40); directoryOffset = u64(rv, 48);
    }
    if (!count || count > this.limits.entries) fail('ZIP 文件数量超过限制，或包内没有文件。');
    if (directorySize > 128 * 1024 * 1024 || directoryOffset + directorySize !== directoryEnd) fail('ZIP 中央目录范围无效。');
    this.directoryOffset = directoryOffset;
    const directory = await this.read(directoryOffset, directorySize);
    const dv = new DataView(directory.buffer, directory.byteOffset, directory.length);
    const names = new Set<string>(); const entries: ZipEntry[] = [];
    let at = 0; let total = 0;
    for (let i = 0; i < count; i++) {
      if (at + 46 > directorySize || dv.getUint32(at, true) !== 0x02014b50) fail('ZIP 中央目录损坏。');
      const flags = dv.getUint16(at + 8, true); const compression = dv.getUint16(at + 10, true);
      let compressedSize = dv.getUint32(at + 20, true); let originalSize = dv.getUint32(at + 24, true);
      let offset = dv.getUint32(at + 42, true);
      const nameSize = dv.getUint16(at + 28, true); const extraSize = dv.getUint16(at + 30, true);
      const next = at + 46 + nameSize + extraSize + dv.getUint16(at + 32, true);
      if (next > directorySize) fail('ZIP 文件名或扩展区越界。');
      if (flags & 1) fail('暂不支持加密 ZIP。');
      if (compression !== 0 && compression !== 8) fail('ZIP 使用了不支持的压缩方法。');
      if (dv.getUint16(at + 34, true)) fail('暂不支持分卷 ZIP。');
      if ([compressedSize, originalSize, offset].includes(0xffffffff)) {
        let extra = at + 46 + nameSize; const extraEnd = extra + extraSize; let found = false;
        while (extra + 4 <= extraEnd) {
          const length = dv.getUint16(extra + 2, true); const stop = extra + 4 + length;
          if (stop > extraEnd) fail('ZIP64 扩展记录越界。');
          if (dv.getUint16(extra, true) === 1) {
            const zv = new DataView(directory.buffer, directory.byteOffset + extra + 4, length); let p = 0;
            if (originalSize === 0xffffffff) { originalSize = u64(zv, p); p += 8; }
            if (compressedSize === 0xffffffff) { compressedSize = u64(zv, p); p += 8; }
            if (offset === 0xffffffff) offset = u64(zv, p);
            found = true; break;
          }
          extra = stop;
        }
        if (!found) fail('缺少 ZIP64 文件大小记录。');
      }
      const nameBytes = directory.slice(at + 46, at + 46 + nameSize);
      const originalName = decodeZipName(nameBytes, flags, directory.subarray(at + 46 + nameSize, at + 46 + nameSize + extraSize));
      const name = originalName.replaceAll('\\', '/');
      if (!name || name.startsWith('/') || /[\u0000-\u001f:]/u.test(name)
        || name.split('/').some(p => ['.', '..', '__proto__', 'constructor', 'prototype'].includes(p))) fail('ZIP 包含不安全的文件路径。');
      const canonical = name.toLowerCase();
      if (names.has(canonical)) fail('ZIP 中存在重复文件名。');
      names.add(canonical);
      if (originalSize > this.limits.entryBytes) fail('ZIP 中单个文件超过解压大小限制。');
      total += originalSize;
      if (!Number.isSafeInteger(total) || total > this.limits.uncompressedBytes) fail('ZIP 解压后的总大小超过限制。');
      if (offset + 30 + compressedSize > directoryOffset) fail('ZIP 本地文件记录越界。');
      entries.push({ name, originalName, originalSize, compressedSize, offset, flags, compression, crc: dv.getUint32(at + 16, true), nameBytes });
      at = next;
    }
    if (at !== directorySize) fail('ZIP 中央目录大小不一致。');
    this.entries = entries;
    return entries;
  }
  async *chunks(entry: ZipEntry): AsyncGenerator<Uint8Array> {
    const header = await this.read(entry.offset, 30);
    const hv = new DataView(header.buffer, header.byteOffset, header.length);
    if (hv.getUint32(0, true) !== 0x04034b50 || hv.getUint16(6, true) !== entry.flags || hv.getUint16(8, true) !== entry.compression) fail('ZIP 本地文件头与中央目录不一致。');
    const nameSize = hv.getUint16(26, true);
    const localName = await this.read(entry.offset + 30, nameSize);
    if (localName.length !== entry.nameBytes.length || localName.some((b, i) => b !== entry.nameBytes[i])) fail('ZIP 文件名记录不一致。');
    if (!(entry.flags & 8) && (hv.getUint32(14, true) !== entry.crc
      || (hv.getUint32(18, true) !== 0xffffffff && hv.getUint32(18, true) !== entry.compressedSize)
      || (hv.getUint32(22, true) !== 0xffffffff && hv.getUint32(22, true) !== entry.originalSize))) fail('ZIP 本地文件校验记录与中央目录不一致。');
    const start = entry.offset + 30 + nameSize + hv.getUint16(28, true);
    if (start + entry.compressedSize > this.directoryOffset) fail('ZIP 数据范围无效。');
    let crc = 0xffffffff; let size = 0; let output: Uint8Array[] = [];
    const accept = (data: Uint8Array) => {
      size += data.length;
      if (size > entry.originalSize || size > this.limits.entryBytes) fail('ZIP 实际解压大小超过文件目录。');
      for (let i = 0; i < data.length; i++) crc = crcTable[(crc ^ data[i]) & 255] ^ (crc >>> 8);
      output.push(data);
    };
    const inflater = entry.compression === 8 ? new Inflate(data => accept(data)) : null;
    const step = 1024 * 1024;
    for (let offset = 0; offset < entry.compressedSize; offset += step) {
      const data = await this.read(start + offset, Math.min(step, entry.compressedSize - offset));
      if (inflater) {
        for (let part = 0; part < data.length; part += 16 * 1024) {
          const piece = data.subarray(part, Math.min(part + 16 * 1024, data.length));
          inflater.push(piece, offset + part + piece.length === entry.compressedSize);
          for (const chunk of output) yield chunk;
          output = [];
        }
      } else {
        accept(data);
        for (const chunk of output) yield chunk;
        output = [];
      }
    }
    if (entry.compressedSize === 0 && inflater) inflater.push(new Uint8Array(), true);
    for (const chunk of output) yield chunk;
    if (size !== entry.originalSize) fail('ZIP 实际解压大小与文件目录不一致。');
    if (((crc ^ 0xffffffff) >>> 0) !== entry.crc) fail('ZIP 文件校验失败，模型包可能损坏。');
  }
}
