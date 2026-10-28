import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { lstat, mkdir, open, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const MAX_INSTALLER_BYTES = 2 ** 31 - 1;
export const MAX_MANIFEST_BYTES = 64 * 1024;
export const PUBLISHER = '崛亮视觉';
const VERSION = /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$/;
const PRODUCTS = {
  viewer: { id: 'local.bim.modelviewer', prefix: 'viewer-v', fileName: v => `Bright.Vision-${v}-Setup.exe` },
  revit: { id: 'BrightVision.RevitExporter', prefix: 'revit-v', fileName: v => `BV-${v}-Setup.exe` },
};
const samePath = (a, b) => process.platform === 'win32' ? path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase() : path.resolve(a) === path.resolve(b);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const exactKeys = (value, keys) => value && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).every(key => keys.includes(key));
function version(value) { if (!VERSION.test(value)) throw new Error('Version must be numeric x.y.z without leading zeroes.'); return value; }
export function repositoryIdentity(value) {
  if (typeof value !== 'string' || value.length > 140) throw new Error('Repository must be OWNER/REPO.');
  const parts = value.split('/');
  if (parts.length !== 2 || !/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/.test(parts[0]) || parts[0].includes('--')
    || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(parts[1]) || parts[1].endsWith('.git')) throw new Error('Repository must be a plain GitHub OWNER/REPO, not a URL or local path.');
  return { owner: parts[0], repository: parts[1] };
}
async function plainAncestors(file, mustExist = true) {
  const full = path.resolve(file);
  for (let cursor = full; cursor; cursor = path.dirname(cursor)) {
    try { const info = await lstat(cursor); if (info.isSymbolicLink()) throw new Error(`Symlink/junction is not an approved release path: ${cursor}`); }
    catch (error) { if (error.code !== 'ENOENT' || mustExist && cursor === full) throw error; }
    if (path.dirname(cursor) === cursor) break;
  }
  if (mustExist && !samePath(await realpath(full), full)) throw new Error('Release path is redirected.');
  return full;
}
const unchanged = (a, b) => a.dev === b.dev && a.ino === b.ino && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
async function describeInstaller(file, destination) {
  const source = await plainAncestors(file);
  const before = await lstat(source, { bigint: true });
  if (!before.isFile() || before.nlink !== 1n || before.size < 2n || before.size > BigInt(MAX_INSTALLER_BYTES)) throw new Error('Installer must be a regular, singly linked file below 2 GiB.');
  const input = await open(source, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  let output;
  try {
    if (!unchanged(before, await input.stat({ bigint: true }))) throw new Error('Installer changed before reading.');
    const hash = createHash('sha256'), buffer = Buffer.alloc(1024 * 1024);
    let bytes = 0, prefix = Buffer.alloc(0);
    if (destination) output = await open(destination, 'wx', 0o600);
    for (;;) {
      const result = await input.read(buffer, 0, buffer.length, null);
      if (result.bytesRead === 0) break;
      if (prefix.length < 2) { prefix = Buffer.concat([prefix, buffer.subarray(0, Math.min(result.bytesRead, 2 - prefix.length))]); if (prefix.length === 2 && (prefix[0] !== 0x4d || prefix[1] !== 0x5a)) throw new Error('Expected a Windows PE installer (MZ header); this is not a signature or malware check.'); }
      bytes += result.bytesRead;
      if (bytes > Number(before.size) || bytes > MAX_INSTALLER_BYTES) throw new Error('Installer grew while being read.');
      const chunk = buffer.subarray(0, result.bytesRead); hash.update(chunk);
      if (output) {
        let written = 0;
        while (written < chunk.length) { const part = await output.write(chunk, written, chunk.length - written, null); if (!part.bytesWritten) throw new Error('Short installer write.'); written += part.bytesWritten; }
      }
    }
    if (bytes !== Number(before.size) || !unchanged(before, await input.stat({ bigint: true })) || !unchanged(before, await lstat(source, { bigint: true }))) throw new Error('Installer changed while hashing/copying.');
    await plainAncestors(source);
    if (output) await output.sync();
    return { size: bytes, sha256: hash.digest('hex') };
  } finally { await input.close(); if (output) await output.close(); }
}

// Offline only: hashes/copies already reviewed final bytes. No network,
// signing, certificate discovery, executable launch, .git write or installation.
export async function prepareGitHubRelease({ repository, product, channel, version: releaseVersion, installer, outputDirectory, revitExporterVersion } = {}) {
  const identity = repositoryIdentity(repository), selected = Object.hasOwn(PRODUCTS, product) && PRODUCTS[product];
  if (!selected) throw new Error('Product must be viewer or revit.');
  if (channel !== 'stable' && channel !== 'preview') throw new Error('Channel must be stable or preview.');
  version(releaseVersion);
  if (typeof installer !== 'string' || !path.isAbsolute(installer) || path.basename(installer) !== selected.fileName(releaseVersion)) throw new Error('Installer must have the exact product/version basename and an absolute path.');
  if (typeof outputDirectory !== 'string' || !path.isAbsolute(outputDirectory)) throw new Error('Output directory must be absolute and must not already exist.');
  if (revitExporterVersion !== undefined) { if (product !== 'viewer') throw new Error('Only a unified viewer can declare an embedded Revit exporter.'); version(revitExporterVersion); }
  const source = await plainAncestors(installer), output = await plainAncestors(outputDirectory, false);
  try { await lstat(output); throw new Error('Output already exists; do not overwrite a reviewed release.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const original = await describeInstaller(source);
  const tag = `${selected.prefix}${releaseVersion}`, fileName = selected.fileName(releaseVersion);
  const base = `https://github.com/${identity.owner}/${identity.repository}/releases`;
  const manifest = {
    schemaVersion: 2, productId: selected.id, publisher: PUBLISHER, channel, version: releaseVersion,
    release: { ...identity, tag, url: `${base}/tag/${tag}` },
    installer: { fileName, ...original, url: `${base}/download/${tag}/${fileName}` },
    ...(revitExporterVersion !== undefined ? { components: { revitExporterVersion } } : {}),
  };
  const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;
  if (Buffer.byteLength(manifestText) > MAX_MANIFEST_BYTES) throw new Error('Manifest exceeds 64 KiB.');
  // Only create this final leaf. A caller supplies an existing, approved parent.
  await mkdir(output);
  const installed = await describeInstaller(source, path.join(output, fileName));
  if (installed.sha256 !== original.sha256 || installed.size !== original.size) throw new Error('Installer changed since preflight; output left for inspection, not publishing.');
  const copied = await describeInstaller(path.join(output, fileName));
  if (copied.sha256 !== original.sha256 || copied.size !== original.size) throw new Error('Copied bytes differ; output left for inspection.');
  await writeFile(path.join(output, 'update.json'), manifestText, { flag: 'wx' });
  const sums = `${original.sha256}  ${fileName}\n${sha(Buffer.from(manifestText))}  update.json\n`;
  await writeFile(path.join(output, 'SHA256SUMS.txt'), sums, { flag: 'wx' });
  const notes = `# 崛亮视觉 · Bright Vision ${releaseVersion}\n\n渠道：${channel === 'preview' ? '开发预览' : '稳定版'}。\n\n本发布目录已按安装包最终字节生成长度与 SHA-256；哈希校验不等于发布者签名或病毒扫描。功能、支持的 Revit 年份与实机验收范围请填写后再发布。\n\n- 产品：${selected.id}\n- 安装包：${fileName}\n- 大小：${original.size} 字节\n- SHA-256：\`${original.sha256}\`\n${revitExporterVersion ? `- 内嵌 Revit 导出插件：${revitExporterVersion}\n` : ''}\n版权及署名：崛亮视觉 / Bright Vision。许可证与 NOTICE 以仓库正式文件为准。\n\n本文件由本地准备脚本生成；未创建或发布 GitHub Release。\n`;
  await writeFile(path.join(output, 'release-notes.md'), notes, { flag: 'wx' });
  const descriptor = { schemaVersion: 1, repository, product, channel, version: releaseVersion, tag, manifest: 'update.json', assets: [fileName, 'update.json', 'SHA256SUMS.txt', 'release-notes.md'], publicReleaseCreated: false, signatureValidation: 'not-performed-by-node-generator' };
  await writeFile(path.join(output, 'release-descriptor.json'), `${JSON.stringify(descriptor, null, 2)}\n`, { flag: 'wx' });
  return { outputDirectory: output, manifest, descriptor };
}

export async function verifyPreparedRelease(directory) {
  const root = await plainAncestors(directory);
  const descriptorPath = path.join(root, 'release-descriptor.json'); await plainAncestors(descriptorPath);
  if ((await stat(descriptorPath)).size > MAX_MANIFEST_BYTES) throw new Error('Release descriptor exceeds 64 KiB.');
  const descriptor = JSON.parse(await readFile(descriptorPath, 'utf8'));
  const identity = repositoryIdentity(descriptor.repository), product = Object.hasOwn(PRODUCTS, descriptor.product) && PRODUCTS[descriptor.product];
  if (!exactKeys(descriptor, ['schemaVersion','repository','product','channel','version','tag','manifest','assets','publicReleaseCreated','signatureValidation']) || !product || descriptor.schemaVersion !== 1 || descriptor.publicReleaseCreated !== false || descriptor.signatureValidation !== 'not-performed-by-node-generator' || !['preview', 'stable'].includes(descriptor.channel)) throw new Error('Unexpected release descriptor.');
  version(descriptor.version);
  const manifestFile = path.join(root, 'update.json'); await plainAncestors(manifestFile);
  if ((await stat(manifestFile)).size > MAX_MANIFEST_BYTES) throw new Error('Manifest exceeds 64 KiB.');
  const manifestBytes = await readFile(manifestFile), manifest = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifestBytes));
  if (!exactKeys(manifest, ['schemaVersion','productId','publisher','channel','version','release','installer','components']) || !exactKeys(manifest.release, ['owner','repository','tag','url']) || !exactKeys(manifest.installer, ['fileName','size','sha256','url'])
    || manifest.components !== undefined && (descriptor.product !== 'viewer' || !exactKeys(manifest.components, ['revitExporterVersion']) || typeof manifest.components.revitExporterVersion !== 'string' || !VERSION.test(manifest.components.revitExporterVersion))) throw new Error('Unexpected manifest fields or embedded component identity; scripts and commands are not a release contract.');
  const fileName = product.fileName(descriptor.version), tag = product.prefix + descriptor.version;
  const releaseBase = `https://github.com/${identity.owner}/${identity.repository}/releases`;
  if (descriptor.tag !== tag || descriptor.manifest !== 'update.json' || JSON.stringify(descriptor.assets) !== JSON.stringify([fileName, 'update.json', 'SHA256SUMS.txt', 'release-notes.md'])) throw new Error('Descriptor contains unexpected assets or tag.');
  if (manifest.schemaVersion !== 2 || manifest.publisher !== PUBLISHER || manifest.productId !== product.id || manifest.version !== descriptor.version || manifest.channel !== descriptor.channel
    || JSON.stringify(manifest.release) !== JSON.stringify({ ...identity, tag, url: `${releaseBase}/tag/${tag}` })
    || manifest.installer?.fileName !== fileName || manifest.installer.url !== `${releaseBase}/download/${tag}/${fileName}`) throw new Error('Manifest identity, channel or fixed GitHub URL differs from its descriptor.');
  const installer = await describeInstaller(path.join(root, fileName));
  if (installer.size !== manifest.installer.size || installer.sha256 !== manifest.installer.sha256) throw new Error('Prepared installer size/hash differs from manifest.');
  const expectedSums = `${installer.sha256}  ${fileName}\n${sha(manifestBytes)}  update.json\n`;
  const sumsPath = path.join(root, 'SHA256SUMS.txt'); await plainAncestors(sumsPath);
  if ((await stat(sumsPath)).size > 1024 || await readFile(sumsPath, 'utf8') !== expectedSums) throw new Error('Checksum file differs from current exact manifest/installer bytes.');
  const notesPath = path.join(root, 'release-notes.md'); await plainAncestors(notesPath);
  if ((await stat(notesPath)).size > MAX_MANIFEST_BYTES) throw new Error('Release notes exceed 64 KiB.');
  return { directory: root, descriptor, manifest, installer };
}

async function main(argv) {
  if (argv[0] === '--verify' && argv.length === 2) return verifyPreparedRelease(path.resolve(argv[1]));
  const values = {};
  const allowed = new Map([['--repository', 'repository'], ['--product', 'product'], ['--channel', 'channel'], ['--version', 'version'], ['--installer', 'installer'], ['--output', 'outputDirectory'], ['--revit-version', 'revitExporterVersion']]);
  for (let index = 0; index < argv.length; index += 2) {
    const key = allowed.get(argv[index]), value = argv[index + 1];
    if (!key || !value || values[key] !== undefined) throw new Error('Expected unique --repository OWNER/REPO --product viewer|revit --channel preview|stable --version X.Y.Z --installer ABSOLUTE_FILE --output NEW_ABSOLUTE_DIRECTORY [--revit-version X.Y.Z].');
    values[key] = value;
  }
  return prepareGitHubRelease(values);
}
if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  try { console.log(JSON.stringify(await main(process.argv.slice(2)), null, 2)); }
  catch (error) { console.error(String(error?.message ?? error)); process.exitCode = 1; }
}
