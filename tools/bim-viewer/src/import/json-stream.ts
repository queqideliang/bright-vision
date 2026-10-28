/** Decode one top-level array item/object member at a time, including split UTF-8 and escape sequences. */
export async function* jsonRecords(chunks: AsyncIterable<Uint8Array>, array: boolean, where: string): AsyncGenerator<[string, unknown]> {
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let started = false; let ended = false; let quoted = false; let escaped = false;
  let depth = 0; let record = ''; let index = 0; let afterComma = false;
  const close = array ? ']' : '}';
  function parse(): [string, unknown] {
    const text = record.trim(); record = '';
    if (!text) throw new Error(`${where} 不是有效的 UTF-8 JSON。`);
    if (array) return [String(index++), JSON.parse(text)];
    const row = JSON.parse(`{${text}}`) as Record<string, unknown>;
    const keys = Object.keys(row);
    if (keys.length !== 1) throw new Error(`${where} 包含无效记录。`);
    return [keys[0]!, row[keys[0]!]];
  }
  try {
    for await (const bytes of chunks) {
      const text = decoder.decode(bytes, { stream: true });
      let pieceStart = 0;
      for (let i = 0; i < text.length; i++) {
        const c = text[i]!;
        if (!started) {
          pieceStart = i + 1;
          if (/\s|\uFEFF/u.test(c)) continue;
          if (c !== (array ? '[' : '{')) throw new Error();
          started = true; continue;
        }
        if (ended) { pieceStart = i + 1; if (!/\s/u.test(c)) throw new Error(); continue; }
        if (quoted) {
          if (escaped) escaped = false;
          else if (c === '\\') escaped = true;
          else if (c === '"') quoted = false;
          continue;
        }
        if (c === '"') quoted = true;
        else if ((c === ',' || c === close) && depth === 0) {
          record += text.slice(pieceStart, i); pieceStart = i + 1;
          if (record.trim()) { yield parse(); afterComma = c === ','; }
          else if (c === ',' || afterComma) throw new Error();
          if (c === close) ended = true;
        } else if (c === '[' || c === '{') depth++;
        else if (c === ']' || c === '}') { depth--; if (depth < 0) throw new Error(); }
      }
      if (started && !ended) record += text.slice(pieceStart);
      if (record.length > 64 * 1024 * 1024) throw new Error(`${where} 单条记录超过 64 MiB，请细分该构件几何。`);
    }
    decoder.decode();
    if (!started || !ended || quoted || depth !== 0) throw new Error();
  } catch (error) {
    if (error instanceof Error && /ZIP|64 MiB/u.test(error.message)) throw error;
    throw new Error(`${where} 不是有效的 UTF-8 JSON。`);
  }
}
