'use strict';

// Local, user-selected feeds only. A SHA-256 proves byte integrity, not who
// published a package. This module never launches an installer or a subprocess.
const fs = require('node:fs/promises');
const { constants } = require('node:fs');
const path = require('node:path');
const { createHash, randomUUID } = require('node:crypto');

const PRODUCT_ID = 'local.bim.modelviewer';
const MAX_INSTALLER_BYTES = 2 * 1024 * 1024 * 1024;
const MAX_MANIFEST_BYTES = 64 * 1024;
const VERSION = /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$/;

class UpdateError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

function parseVersion(value) {
  if (typeof value !== 'string' || !VERSION.test(value)) throw new UpdateError('invalid-version', '更新版本必须是 x.y.z 格式。');
  return value.split('.').map(Number);
}

function compareVersions(a, b) {
  const left = parseVersion(a), right = parseVersion(b);
  for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return left[i] > right[i] ? 1 : -1;
  return 0;
}

function installerFileNames(version) {
  parseVersion(version);
  // Keep the legacy basename for already installed 0.4 clients, while newer
  // clients can also consume the single-product Bright.Vision installer name.
  return [`Bright.Vision-${version}-Setup.exe`, `BIM-Model-Viewer-${version}-Setup.exe`];
}

function absolutePath(value) {
  if (typeof value !== 'string' || !value || value.length > 32760 || value.includes('\0') || !path.isAbsolute(value)
    || /^\\\\[?.]\\/.test(value)) throw new UpdateError('unsafe-path', '请选择本机或共享目录作为更新来源。');
  return path.resolve(value);
}

function samePath(a, b) {
  const normalize = value => path.normalize(value).replace(/[\\/]+$/, '');
  return process.platform === 'win32' ? normalize(a).toLowerCase() === normalize(b).toLowerCase() : normalize(a) === normalize(b);
}

function identity(a, b) {
  return a.dev === b.dev && a.ino === b.ino && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
}

// lstat each existing component: this rejects file symlinks and Windows
// directory junctions, including links in ancestors. realpath also rejects a
// redirected path. Node does not expose arbitrary Windows reparse-tag metadata.
async function plainPath(file, type, createDirectories = false) {
  const target = absolutePath(file);
  const root = path.parse(target).root;
  const parts = target.slice(root.length).split(path.sep).filter(Boolean);
  let current = root, result = await fs.lstat(root, { bigint: true });
  if (result.isSymbolicLink() || !result.isDirectory()) throw new UpdateError('unsafe-path', '更新路径不能包含链接或重定向目录。');
  for (let index = 0; index < parts.length; index++) {
    current = path.join(current, parts[index]);
    try { result = await fs.lstat(current, { bigint: true }); }
    catch (error) {
      if (!createDirectories || error.code !== 'ENOENT') throw error;
      try { await fs.mkdir(current, { mode: 0o700 }); } catch (creation) { if (creation.code !== 'EEXIST') throw creation; }
      result = await fs.lstat(current, { bigint: true });
    }
    const directory = index < parts.length - 1 || type === 'directory';
    if (result.isSymbolicLink() || (directory ? !result.isDirectory() : !result.isFile()))
      throw new UpdateError('unsafe-path', '更新路径不能包含链接或特殊文件。');
  }
  if (!samePath(await fs.realpath(target), target)) throw new UpdateError('unsafe-path', '更新路径发生了重定向。');
  return result;
}

async function openPlainFile(file, maximum, expectedSize) {
  const before = await plainPath(file, 'file');
  if (before.nlink > 1n) throw new UpdateError('unsafe-file', '更新文件不能使用硬链接。');
  if (before.size < 1n || before.size > BigInt(maximum) || (expectedSize !== undefined && before.size !== BigInt(expectedSize)))
    throw new UpdateError('size-mismatch', '更新文件大小无效或与清单不一致。');
  // O_NOFOLLOW is unavailable on Windows; handle identity and path checks are
  // repeated after open and read instead of claiming a Windows no-follow flag.
  const handle = await fs.open(file, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    const opened = await handle.stat({ bigint: true });
    if (!opened.isFile() || !identity(before, opened) || opened.nlink > 1n) throw new UpdateError('file-changed', '更新文件在检查时发生了变化。');
    const after = await plainPath(file, 'file');
    if (!identity(opened, after)) throw new UpdateError('file-changed', '更新文件在检查时发生了变化。');
    return { handle, snapshot: opened };
  }
  catch (error) { await handle.close(); throw error; }
}

async function unchanged(file, opened) {
  const handleNow = await opened.handle.stat({ bigint: true });
  const pathNow = await plainPath(file, 'file');
  if (!identity(opened.snapshot, handleNow) || !identity(opened.snapshot, pathNow) || handleNow.nlink > 1n)
    throw new UpdateError('file-changed', '更新文件在读取时发生了变化，请重新检查。');
}

async function readManifest(file, maximum) {
  const opened = await openPlainFile(file, maximum);
  try {
    // A fixed-size buffer keeps a growing manifest from causing an unbounded
    // allocation. Read one extra byte to detect appends before the final stat.
    const buffer = Buffer.alloc(Number(opened.snapshot.size) + 1);
    let count = 0;
    while (count < buffer.length) {
      const { bytesRead } = await opened.handle.read(buffer, count, buffer.length - count, null);
      if (!bytesRead) break;
      count += bytesRead;
    }
    await unchanged(file, opened);
    if (count !== Number(opened.snapshot.size)) throw new UpdateError('file-changed', '更新清单在读取时发生了变化。');
    try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(buffer.subarray(0, count)).replace(/^\uFEFF/, '')); }
    catch { throw new UpdateError('invalid-manifest', '更新清单不是有效的 UTF-8 JSON。'); }
  }
  finally { await opened.handle.close(); }
}

function validateManifest(value, maximum) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || value.schemaVersion !== 1 || value.productId !== PRODUCT_ID || value.channel !== 'stable')
    throw new UpdateError('invalid-manifest', '更新清单的产品、渠道或格式不匹配。');
  parseVersion(value.version);
  const installer = value.installer;
  if (!installer || typeof installer !== 'object' || Array.isArray(installer)
    || !installerFileNames(value.version).includes(installer.fileName))
    throw new UpdateError('unsafe-installer-name', '更新安装包名称无效。');
  if (typeof installer.sha256 !== 'string' || !/^[a-f\d]{64}$/i.test(installer.sha256)) throw new UpdateError('invalid-manifest', '更新清单缺少有效的 SHA-256。');
  if (!Number.isSafeInteger(installer.size) || installer.size < 1 || installer.size > maximum) throw new UpdateError('size-mismatch', '更新清单中的安装包大小无效。');
  return { version: value.version, installer: { fileName: installer.fileName, sha256: installer.sha256.toLowerCase(), size: installer.size } };
}

async function transferAndHash(file, installer, maximum, destination) {
  const opened = await openPlainFile(file, maximum, installer.size);
  try {
    const hash = createHash('sha256'), buffer = Buffer.alloc(1024 * 1024);
    let total = 0;
    for (;;) {
      const { bytesRead } = await opened.handle.read(buffer, 0, buffer.length, null);
      if (!bytesRead) break;
      total += bytesRead;
      if (total > installer.size || total > maximum) throw new UpdateError('size-mismatch', '更新安装包在读取时超出预期大小。');
      hash.update(buffer.subarray(0, bytesRead));
      if (destination) {
        let written = 0;
        while (written < bytesRead) {
          const result = await destination.write(buffer, written, bytesRead - written, null);
          if (!result.bytesWritten) throw new UpdateError('write-failed', '无法完整保存更新安装包。');
          written += result.bytesWritten;
        }
      }
    }
    await unchanged(file, opened);
    if (total !== installer.size) throw new UpdateError('size-mismatch', '更新安装包大小与清单不一致。');
    if (hash.digest('hex') !== installer.sha256) throw new UpdateError('hash-mismatch', '更新安装包 SHA-256 校验失败。');
  }
  finally { await opened.handle.close(); }
}

function createUpdateService({ currentVersion, userDataPath, maxInstallerBytes = MAX_INSTALLER_BYTES, maxManifestBytes = MAX_MANIFEST_BYTES } = {}) {
  parseVersion(currentVersion);
  const userData = absolutePath(userDataPath);
  if (!Number.isSafeInteger(maxInstallerBytes) || maxInstallerBytes < 1 || maxInstallerBytes > MAX_INSTALLER_BYTES
    || !Number.isSafeInteger(maxManifestBytes) || maxManifestBytes < 1 || maxManifestBytes > MAX_MANIFEST_BYTES)
    throw new TypeError('Invalid update size limits.');
  let candidate = null, staged = null, busy = false;
  let state = { status: 'not-configured', currentVersion, latestVersion: null, code: 'not-configured', message: '请选择可信的更新目录。', checkedAt: null };
  const publicState = value => ({ ...value, ...(value.installer ? { installer: { ...value.installer } } : {}) });
  const result = (status, code, message, extra = {}) => {
    state = { status, currentVersion, latestVersion: candidate?.version || null, code, message, checkedAt: state.checkedAt, ...(candidate ? { installer: { ...candidate.installer } } : {}), ...extra };
    return publicState(state);
  };
  const failure = error => result('error', error instanceof UpdateError ? error.code : error.code === 'ENOENT' ? 'not-found' : 'io-error',
    error instanceof UpdateError ? error.message : error.code === 'ENOENT' ? '更新目录或文件不存在。' : '无法读取或保存更新文件。');
  async function run(action) {
    if (busy) return { ...publicState(state), status: 'error', code: 'busy', message: '正在检查或准备更新，请稍候。' };
    busy = true;
    try { return await action(); } catch (error) { return failure(error); } finally { busy = false; }
  }
  async function verify() {
    if (!staged) throw new UpdateError('not-staged', '尚未准备好经过校验的更新安装包。');
    try { await transferAndHash(staged.path, staged.installer, maxInstallerBytes); }
    catch (error) { staged = null; throw error; }
    return result('staged', 'staged', '更新安装包已校验，等待用户确认安装。', { stagedPath: staged.path });
  }
  return {
    getState: () => publicState(state),
    check: feedDirectory => run(async () => {
      candidate = null; staged = null;
      state = { ...state, checkedAt: new Date().toISOString() };
      if (feedDirectory === undefined || feedDirectory === null || feedDirectory === '') return result('not-configured', 'not-configured', '请选择可信的更新目录。');
      const feed = absolutePath(feedDirectory);
      await plainPath(feed, 'directory');
      const manifest = validateManifest(await readManifest(path.join(feed, 'update.json'), maxManifestBytes), maxInstallerBytes);
      const comparison = compareVersions(manifest.version, currentVersion);
      if (comparison <= 0) return result('up-to-date', comparison === 0 ? 'same-version' : 'older-version', '当前已是此更新源的最新版本。', { latestVersion: manifest.version });
      await transferAndHash(path.join(feed, manifest.installer.fileName), manifest.installer, maxInstallerBytes);
      candidate = { ...manifest, feed };
      return result('update-available', 'update-available', '发现新版本，可由用户选择准备并安装。');
    }),
    stage: () => run(async () => {
      if (!candidate) throw new UpdateError('no-verified-update', '请先检查并验证新版本安装包。');
      if (staged) return verify();
      const cache = path.join(userData, 'update-cache');
      await plainPath(cache, 'directory', true);
      const directory = path.join(cache, `${candidate.version}-${randomUUID()}`);
      await fs.mkdir(directory, { mode: 0o700 });
      const temporary = path.join(directory, '.installer.tmp');
      const output = path.join(directory, candidate.installer.fileName);
      let handle, renamed = false;
      try {
        await plainPath(directory, 'directory');
        handle = await fs.open(temporary, 'wx', 0o600);
        await transferAndHash(path.join(candidate.feed, candidate.installer.fileName), candidate.installer, maxInstallerBytes, handle);
        await handle.sync(); await handle.close(); handle = null;
        await plainPath(directory, 'directory');
        await fs.rename(temporary, output); renamed = true;
        staged = { path: output, installer: { ...candidate.installer } };
        return await verify();
      }
      catch (error) {
        if (handle) await handle.close().catch(() => {});
        staged = null;
        // Only known files in this operation's randomly named directory are
        // removed; never recursively delete or traverse a changed directory.
        try {
          await plainPath(directory, 'directory');
          await fs.unlink(renamed ? output : temporary).catch(() => {});
          await fs.rmdir(directory).catch(() => {});
        } catch { /* A redirected directory is left untouched. */ }
        throw error;
      }
    }),
    verifyStaged: () => run(verify),
  };
}

module.exports = { createUpdateService, compareVersions, installerFileNames, PRODUCT_ID, MAX_INSTALLER_BYTES, MAX_MANIFEST_BYTES };
