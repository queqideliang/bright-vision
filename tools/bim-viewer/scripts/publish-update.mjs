import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, writeFile, lstat, mkdir, copyFile, rename, unlink } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
const { installerFileNames, PRODUCT_ID, MAX_INSTALLER_BYTES } = require('../electron/update-service.cjs');
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function sameFilePath(left, right) {
  return process.platform === 'win32' ? path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase() : path.resolve(left) === path.resolve(right);
}

async function describeInstaller(file) {
  const before = await lstat(file, { bigint: true });
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1n || before.size < 1n || before.size > BigInt(MAX_INSTALLER_BYTES))
    throw new Error('Installer must be a nonempty regular file within the update size limit.');
  const hash = createHash('sha256');
  let count = 0;
  for await (const chunk of createReadStream(file)) {
    count += chunk.length;
    if (count > MAX_INSTALLER_BYTES) throw new Error('Installer grew beyond the update size limit.');
    hash.update(chunk);
  }
  const after = await lstat(file, { bigint: true });
  if (before.dev !== after.dev || before.ino !== after.ino || before.size !== after.size || before.mtimeNs !== after.mtimeNs || before.ctimeNs !== after.ctimeNs || after.nlink > 1n || BigInt(count) !== after.size)
    throw new Error('Installer changed while being hashed.');
  return { size: count, sha256: hash.digest('hex') };
}

// Both names contain the very same unified installer bytes. The feed continues
// to name the legacy ordinary copy so an installed 0.4 updater can accept it;
// modern updaters also accept a feed using the Bright.Vision basename. Never
// use hard links: updater validation intentionally rejects multiply linked files.
export async function publishUpdate({ installer, outputs, version }) {
  const [brightName, legacyName] = installerFileNames(version);
  if (typeof installer !== 'string' || ![brightName, legacyName].includes(path.basename(installer)))
    throw new Error(`Installer must be named ${brightName} or ${legacyName}`);
  if (!Array.isArray(outputs) || !outputs.length || outputs.some(output => typeof output !== 'string' || !output))
    throw new Error('Specify at least one output update folder.');
  const source = path.resolve(installer);
  const description = await describeInstaller(source);
  const manifest = { schemaVersion: 1, productId: PRODUCT_ID, channel: 'stable', version,
    installer: { fileName: legacyName, ...description } };
  const published = [];
  for (const output of new Set(outputs.map(value => path.resolve(value)))) {
    await mkdir(output, { recursive: true });
    const tempManifest = path.join(output, `update.${randomUUID()}.tmp`);
    try {
      for (const fileName of [brightName, legacyName]) {
        const destination = path.join(output, fileName);
        const temporary = `${destination}.${randomUUID()}.tmp`;
        try {
          if (!sameFilePath(source, destination)) {
            await copyFile(source, temporary);
            await rename(temporary, destination);
          }
          const copied = await describeInstaller(destination);
          if (copied.size !== description.size || copied.sha256 !== description.sha256) throw new Error('Copied installer verification failed.');
        }
        finally { await unlink(temporary).catch(() => {}); }
      }
      // Publish the manifest only after both complete, verified installer names
      // exist, so old clients never discover a missing compatibility copy.
      await writeFile(tempManifest, `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
      await rename(tempManifest, path.join(output, 'update.json'));
      published.push(output);
    }
    finally { await unlink(tempManifest).catch(() => {}); }
  }
  return { version, manifest, outputs: published, installerNames: [brightName, legacyName] };
}

export async function main(argv = process.argv.slice(2)) {
  const { version } = JSON.parse(await readFile(path.join(project, 'package.json'), 'utf8'));
  const [brightName, legacyName] = installerFileNames(version);
  let installer;
  const outputs = [];
  for (let index = 0; index < argv.length; index += 2) {
    const option = argv[index], value = argv[index + 1];
    if (!value || !['--installer', '--output'].includes(option)) throw new Error('Usage: node scripts/publish-update.mjs [--installer FILE] --output DIRECTORY [--output DIRECTORY]');
    if (option === '--installer') installer = path.resolve(value);
    else outputs.push(path.resolve(value));
  }
  if (!installer) {
    installer = path.join(project, 'release', brightName);
    try { await lstat(installer); }
    catch (error) { if (error.code !== 'ENOENT') throw error; installer = path.join(project, 'release', legacyName); }
  }
  const result = await publishUpdate({ installer, outputs, version });
  for (const output of result.outputs) console.log(`Published ${version}: ${output} (Bright.Vision package and legacy-compatible feed)`);
  return result;
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) await main();
