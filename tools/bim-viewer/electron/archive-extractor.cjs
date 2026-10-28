const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { mkdir, readFile, writeFile, stat, lstat, unlink, rmdir } = require('node:fs/promises');
const crypto = require('node:crypto');
const path = require('node:path');
const { version: productVersion } = require('../package.json');
const execute = promisify(execFile);
const MAX_ARCHIVE = 32 * 1024 * 1024 * 1024;
const MAX_MODEL = 2 * 1024 * 1024 * 1024;
const MAX_HELPER = 16 * 1024 * 1024;
const MAX_MANIFEST = 64 * 1024;

function absoluteDirectory(value) {
  if (typeof value !== 'string' || !path.isAbsolute(value) || value.includes('\0') || value.slice(path.parse(value).root.length).includes(':')) {
    throw new Error('模型缓存或内置读取器目录无效。');
  }
  return path.resolve(value);
}

function inside(directory, candidate) {
  const relative = path.relative(directory, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

// Never execute installed resources redirected through a cache junction.
async function withoutLinks(file, allowMissing = false) {
  const parts = [];
  let current = path.resolve(file);
  while (true) {
    parts.push(current);
    const parent = path.dirname(current);
    if (parent === current) break;
    current = parent;
  }
  for (const part of parts.reverse()) {
    let info;
    try { info = await lstat(part); }
    catch (error) { if (allowMissing && error.code === 'ENOENT') continue; throw error; }
    if (info.isSymbolicLink()) throw new Error('内置读取器和缓存目录不能使用符号链接或目录联接。');
    if (part !== file && !info.isDirectory()) throw new Error('内置读取器目录无效。');
  }
}

function validDigest(item, name, limit) {
  return item && item.name === name && Number.isSafeInteger(item.bytes) && item.bytes > 0 && item.bytes <= limit && /^[a-f0-9]{64}$/.test(item.sha256);
}

async function verifyHelper(helperDirectory) {
  await withoutLinks(helperDirectory);
  if (!(await stat(helperDirectory)).isDirectory()) throw new Error('内置读取器目录无效。');
  const manifestPath = path.join(helperDirectory, 'manifest.json');
  const executable = path.join(helperDirectory, 'ArchiveExtractor.exe');
  await withoutLinks(manifestPath);
  await withoutLinks(executable);
  const manifestInfo = await stat(manifestPath);
  if (!manifestInfo.isFile() || manifestInfo.size <= 0 || manifestInfo.size > MAX_MANIFEST) throw new Error('内置读取器清单大小无效。');
  const manifestBytes = await readFile(manifestPath);
  if (manifestBytes.length !== manifestInfo.size || manifestBytes.length > MAX_MANIFEST) throw new Error('内置读取器清单已变化。');
  let manifest;
  try { manifest = JSON.parse(manifestBytes.toString('utf8')); }
  catch { throw new Error('内置读取器清单格式无效。'); }
  if (!manifest || manifest.schemaVersion !== 1 || manifest.component !== 'archive-extractor' || manifest.productVersion !== productVersion ||
      manifest.framework !== '.NETFramework,Version=v4.5' || !validDigest(manifest.source, 'ArchiveExtractor.cs', 1024 * 1024) ||
      !manifest.compiler || typeof manifest.compiler.path !== 'string' || !path.isAbsolute(manifest.compiler.path) ||
      !Number.isSafeInteger(manifest.compiler.bytes) || manifest.compiler.bytes <= 0 || manifest.compiler.bytes > 64 * 1024 * 1024 ||
      !/^[a-f0-9]{64}$/.test(manifest.compiler.sha256) || !validDigest(manifest.executable, 'ArchiveExtractor.exe', MAX_HELPER)) {
    throw new Error('内置读取器清单与当前应用不匹配，请重新安装正式软件。');
  }
  const info = await stat(executable);
  if (!info.isFile() || info.size !== manifest.executable.bytes || info.size > MAX_HELPER) throw new Error('内置读取器文件缺失或大小已变化，请重新安装正式软件。');
  const bytes = await readFile(executable);
  if (bytes.length !== info.size || crypto.createHash('sha256').update(bytes).digest('hex') !== manifest.executable.sha256) {
    throw new Error('内置读取器完整性检查失败，请重新安装正式软件。');
  }
  // SHA256 checks payload consistency, not publisher identity. This directory
  // must come from the trusted installation, rather than user archive content.
  return executable;
}

function checkCanceled(signal) {
  if (signal && signal.aborted) throw new Error('读取已取消。');
}

async function cleanup(folder) {
  try {
    const info = await lstat(folder);
    if (!info.isDirectory() || info.isSymbolicLink()) return;
    for (const file of ['extract.json', 'model.zip.tmp', 'model.zip']) {
      try { await unlink(path.join(folder, file)); } catch { /* Only owned request data is removed. */ }
    }
    await rmdir(folder);
  } catch { /* Preserve the original extraction error. */ }
}

async function extractArchiveEntry({ input, index, kind, name, root, helperDirectory, signal }) {
  if (typeof input !== 'string' || !path.isAbsolute(input) || !/\.zip$/i.test(input)) throw new Error('请选择本机 ZIP 模型包。');
  if (kind !== 'zip') throw new Error('原始 RVT 尚未支持独立读取，请选择模型 ZIP。');
  if (!Number.isSafeInteger(index) || index < 0 || index >= 1_000_000) throw new Error('模型选择无效。');
  checkCanceled(signal);
  root = absoluteDirectory(root);
  helperDirectory = absoluteDirectory(helperDirectory);
  if (inside(root, helperDirectory) || inside(helperDirectory, root)) throw new Error('内置读取器必须与模型缓存分开安装。');
  const source = await stat(input);
  if (!source.isFile() || source.size > MAX_ARCHIVE) throw new Error('模型包大小无效。');
  const executable = await verifyHelper(helperDirectory);
  await withoutLinks(root, true);
  await mkdir(root, { recursive: true });
  await withoutLinks(root);
  checkCanceled(signal);
  const id = crypto.randomUUID();
  const folder = path.join(root, id);
  await mkdir(folder);
  const output = path.join(folder, 'model.zip');
  const request = path.join(folder, 'extract.json');
  try {
    await writeFile(request, JSON.stringify({ input, index, output, kind }), { flag: 'wx' });
    checkCanceled(signal);
    await execute(executable, [request], { windowsHide: true, ...(signal ? { signal } : {}), maxBuffer: 1024 * 1024 });
    checkCanceled(signal);
    await withoutLinks(output);
    const result = await stat(output);
    if (!result.isFile() || result.size > MAX_MODEL) throw new Error('读取的模型文件大小无效。');
    return { id, path: output, size: result.size, name: typeof name === 'string' ? name.split(/[\\/]/).at(-1).slice(0, 1024) : '模型.zip' };
  } catch (error) {
    await cleanup(folder);
    checkCanceled(signal);
    throw new Error(`无法读取包内模型：${String(error.stderr || error.message || error).slice(0, 4096)}`);
  }
}
module.exports = { extractArchiveEntry };
