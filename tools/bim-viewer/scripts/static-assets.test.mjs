import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { resolveAssetPath, responseHeaders } = require('../electron/static-assets.cjs');
const root = path.resolve('isolated-static-root');

test('only resolves known bundled assets inside the static root', () => {
  assert.equal(resolveAssetPath(root, '/'), path.join(root, 'index.html'));
  assert.equal(resolveAssetPath(root, '/assets/worker.js'), path.join(root, 'assets', 'worker.js'));
  assert.equal(resolveAssetPath(root, '/assets/%E6%A8%A1%E5%9E%8B.png'), path.join(root, 'assets', '模型.png'));
  for (const url of ['/../secret.txt', '/%2e%2e/secret.txt', '/assets/%2e%2e/secret.txt', '/%5c..%5csecret.txt', '/C:/secret.txt', '/%00.png', '/bad%XX.js', '/package.cjs', '/.env', '/']) {
    if (url === '/') continue;
    assert.equal(resolveAssetPath(root, url), null, url);
  }
});

test('worker modules and local textures are allowed while remote connections are excluded by CSP', () => {
  const headers = responseHeaders(path.join(root, 'assets', 'worker.js'));
  assert.equal(headers['Content-Type'], 'text/javascript; charset=utf-8');
  assert.match(headers['Content-Security-Policy'], /worker-src 'self' blob:/);
  assert.match(headers['Content-Security-Policy'], /img-src 'self' blob: data:/);
  assert.match(headers['Content-Security-Policy'], /connect-src 'self'/);
  assert.equal(headers['X-Content-Type-Options'], 'nosniff');
});
