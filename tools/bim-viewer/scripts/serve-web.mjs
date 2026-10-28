import http from 'node:http';
import path from 'node:path';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = fileURLToPath(new URL('../dist/', import.meta.url));
const portArgument = process.argv.find((value) => value.startsWith('--port='));
const port = Number(portArgument?.slice('--port='.length) || '4173');
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('端口必须为 1–65535 的整数。');
try {
  await stat(path.join(root, 'index.html'));
} catch {
  throw new Error('找不到 dist/index.html。请先在 tools/bim-viewer 中运行 npm run build。');
}

const server = http.createServer(async (request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' }).end('Method not allowed');
    return;
  }
  let filePath;
  try {
    const url = new URL(request.url || '/', `http://127.0.0.1:${port}`);
    filePath = resolveAssetPath(root, url.pathname);
  } catch {
    response.writeHead(400).end('Bad request');
    return;
  }
  if (!filePath) { response.writeHead(404).end('Not found'); return; }
  try {
    const bytes = await readFile(filePath);
    response.writeHead(200, { ...responseHeaders(filePath), 'Content-Length': bytes.byteLength });
    response.end(request.method === 'HEAD' ? undefined : bytes);
  } catch {
    response.writeHead(404).end('Not found');
  }
});
server.on('error', (error) => { console.error(error.message); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => {
  console.log(`BIM 模型查看器：http://127.0.0.1:${port}`);
  console.log('仅监听本机。按 Ctrl+C 停止；所选模型不会上传到此服务。');
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close());
