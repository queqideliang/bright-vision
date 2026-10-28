const path = require('node:path');

const CONTENT_SECURITY_POLICY = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' blob: data:",
  "connect-src 'self'",
  "worker-src 'self' blob:",
  "font-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "form-action 'none'",
].join('; ');

const MIME_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.wasm': 'application/wasm',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
  '.md': 'text/plain; charset=utf-8',
};

/** Resolve only bundled files; URL query strings never become filesystem paths. */
function resolveAssetPath(root, pathname) {
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    return null;
  }
  if (!decoded.startsWith('/') || /[\0\\:]/.test(decoded)) return null;
  if (decoded.split('/').some((segment) => segment === '.' || segment === '..')) return null;
  const relative = decoded === '/' ? 'index.html' : decoded.slice(1);
  const resolved = path.resolve(root, relative);
  const within = path.relative(path.resolve(root), resolved);
  if (!within || within === '..' || within.startsWith(`..${path.sep}`) || path.isAbsolute(within)) return null;
  if (!Object.hasOwn(MIME_TYPES, path.extname(resolved).toLowerCase())) return null;
  return resolved;
}

function responseHeaders(filePath) {
  return {
    'Content-Type': MIME_TYPES[path.extname(filePath).toLowerCase()] || 'application/octet-stream',
    'Content-Security-Policy': CONTENT_SECURITY_POLICY,
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Cache-Control': 'no-cache',
  };
}

module.exports = { CONTENT_SECURITY_POLICY, resolveAssetPath, responseHeaders };
