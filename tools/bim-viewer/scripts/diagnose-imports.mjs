import { readFile, open, writeFile } from 'node:fs/promises';
import { parseModelZip } from '../src/import/parse.ts';
const inventory = JSON.parse(await readFile('test-results/archive-inventory.json', 'utf8'));
const report = [];
for (const item of inventory.filter(item => item.components)) {
  const start = performance.now(); const handle = await open(item.path, 'r');
  try {
    const model = await parseModelZip({ size: item.bytes, slice: (start, end) => ({ arrayBuffer: async () => {
      const bytes = new Uint8Array(end - start); const result = await handle.read(bytes, 0, bytes.length, start);
      if (result.bytesRead !== bytes.length) throw new Error('文件读取不完整。');
      return bytes.buffer;
    } }) }, item.path.split(/[\\/]/).at(-1));
    report.push({ file: item.path, stats: model.stats, warnings: model.warnings.length, seconds: +((performance.now() - start) / 1000).toFixed(2) });
  } catch (error) { report.push({ file: item.path, error: error.message }); }
  finally { await handle.close(); }
  console.log(JSON.stringify({ ...report.at(-1), file: item.path.split(/[\\/]/).at(-1) }));
  global.gc?.();
}
await writeFile('test-results/import-report.json', JSON.stringify(report, null, 2));
console.log(JSON.stringify({ tested: report.length, failures: report.filter(row => row.error).length }));
