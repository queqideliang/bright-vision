import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir, mkdtemp, rename, unlink, rmdir, lstat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const execute = promisify(execFile);
const project = fileURLToPath(new URL('..', import.meta.url));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const MAX_HELPER = 16 * 1024 * 1024;

export function assemblyVersion(version) {
  if (typeof version !== 'string' || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) throw new Error('Invalid package version.');
  const parts = version.split('-')[0].split('.');
  if (parts.some(part => Number(part) > 65534)) throw new Error('Package version exceeds assembly version limits.');
  return `${parts.join('.')}.0`;
}

async function fileBytes(file, maximum) {
  const info = await lstat(file);
  if (!info.isFile() || info.isSymbolicLink() || info.size <= 0 || info.size > maximum) throw new Error(`Invalid build input: ${file}`);
  const bytes = await readFile(file);
  if (bytes.length !== info.size) throw new Error(`Build input changed while reading: ${file}`);
  return bytes;
}

// Dependency injection is for isolated build tests. The CLI always uses this
// project's source and the installed Windows Framework compiler.
export async function buildNativeHelper({ projectDirectory = project, platform = process.platform,
  compilerPath = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'), executeCompiler = execute } = {}) {
  if (platform !== 'win32') throw new Error('The native archive helper is built only on Windows. Web builds do not require it.');
  const packageData = JSON.parse(await readFile(path.join(projectDirectory, 'package.json'), 'utf8'));
  const version = assemblyVersion(packageData.version);
  const sourcePath = path.join(projectDirectory, 'native', 'ArchiveExtractor.cs');
  const source = await fileBytes(sourcePath, 1024 * 1024);
  const compiler = await fileBytes(compilerPath, 64 * 1024 * 1024);
  const assemblyMetadata = `using System.Reflection;\n[assembly: AssemblyTitle("bright.vision Archive Extractor")]\n[assembly: AssemblyDescription("Local nested ZIP data extractor")]\n[assembly: AssemblyCompany("崛亮视觉")]\n[assembly: AssemblyProduct("bright.vision")]\n[assembly: AssemblyVersion("${version}")]\n[assembly: AssemblyFileVersion("${version}")]\n[assembly: AssemblyInformationalVersion("${packageData.version}")]\n[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.5", FrameworkDisplayName=".NET Framework 4.5")]\n`;
  const expected = { schemaVersion: 1, component: 'archive-extractor', productVersion: packageData.version,
    source: { name: 'ArchiveExtractor.cs', bytes: source.length, sha256: digest(source) },
    compiler: { path: path.resolve(compilerPath), bytes: compiler.length, sha256: digest(compiler) },
    framework: '.NETFramework,Version=v4.5', assemblyMetadataSha256: digest(Buffer.from(assemblyMetadata, 'utf8')),
    integrityNote: 'SHA256 records payload integrity, not publisher trust' };
  const destination = path.join(projectDirectory, 'native', 'generated');
  const executable = path.join(destination, 'ArchiveExtractor.exe');
  const manifestPath = path.join(destination, 'manifest.json');
  let previousManifest;
  try { previousManifest = JSON.parse((await fileBytes(manifestPath, 65536)).toString('utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw new Error(`Existing helper manifest is invalid: ${error.message}`); }
  if (previousManifest !== undefined) {
    if (!previousManifest || previousManifest.schemaVersion !== 1 || previousManifest.component !== 'archive-extractor' ||
        typeof previousManifest.productVersion !== 'string' || !previousManifest.source || !previousManifest.compiler ||
        !/^[a-f0-9]{64}$/.test(previousManifest.source.sha256) || !/^[a-f0-9]{64}$/.test(previousManifest.compiler.sha256)) {
      throw new Error('Existing helper manifest has an invalid schema; no rebuild attempted.');
    }
    const { executable: existingEntry, ...buildInputs } = previousManifest;
    // An existing build with missing or changed payload is an integrity error,
    // not an invitation to regenerate a file that a scanner may have removed.
    let existing;
    try { existing = await fileBytes(executable, MAX_HELPER); }
    catch (error) { throw new Error(`Existing archive helper is missing or invalid; no rebuild attempted: ${error.message}`); }
    if (!existingEntry || existingEntry.name !== 'ArchiveExtractor.exe' || existingEntry.bytes !== existing.length || existingEntry.sha256 !== digest(existing)) {
      throw new Error('Existing archive helper integrity check failed; no rebuild attempted.');
    }
    if (JSON.stringify(buildInputs) === JSON.stringify(expected)) return { reused: true, directory: destination, manifest: previousManifest };
  } else {
    try { await lstat(executable); throw new Error('An archive helper exists without its manifest; no overwrite attempted.'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  await mkdir(destination, { recursive: true });
  if ((await lstat(destination)).isSymbolicLink()) throw new Error('Native output must not be a symbolic link or junction.');
  const temporary = await mkdtemp(path.join(destination, '.build-'));
  const temporaryExecutable = path.join(temporary, 'ArchiveExtractor.exe');
  const temporaryManifest = path.join(temporary, 'manifest.json');
  const assemblyInfo = path.join(temporary, 'AssemblyInfo.cs');
  const temporarySource = path.join(temporary, 'ArchiveExtractor.cs');
  try {
    await writeFile(temporarySource, source, { flag: 'wx' });
    await writeFile(assemblyInfo, assemblyMetadata, { flag: 'wx' });
    await executeCompiler(compilerPath, ['/nologo', '/target:exe', '/optimize+', '/codepage:65001', '/langversion:5', `/out:${temporaryExecutable}`,
      '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', temporarySource, assemblyInfo],
    { windowsHide: true, maxBuffer: 1024 * 1024 });
    const bytes = await fileBytes(temporaryExecutable, MAX_HELPER);
    const manifest = { ...expected, executable: { name: 'ArchiveExtractor.exe', bytes: bytes.length, sha256: digest(bytes) } };
    await writeFile(temporaryManifest, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
    // If the fixed result is removed by security software, fail. Do not change
    // its name, loader, or location to try to run it anyway.
    await rename(temporaryExecutable, executable);
    await rename(temporaryManifest, manifestPath);
    const installed = await fileBytes(executable, MAX_HELPER);
    if (digest(installed) !== manifest.executable.sha256) throw new Error('Built archive helper is missing or changed after installation.');
    return { reused: false, directory: destination, manifest };
  } finally {
    for (const file of [temporaryExecutable, temporaryManifest, temporarySource, assemblyInfo]) {
      try { await unlink(file); } catch (error) { if (error.code !== 'ENOENT') console.warn(`Unable to clean build input: ${error.message}`); }
    }
    try { await rmdir(temporary); } catch (error) { console.warn(`Unable to clean build directory: ${error.message}`); }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = await buildNativeHelper();
    console.log(JSON.stringify({ nativeHelper: result.reused ? 'reused' : 'built', directory: result.directory,
      productVersion: result.manifest.productVersion, executable: result.manifest.executable,
      publisherTrust: 'unsigned-development-build; hash-is-not-signature',
      releaseSigning: 'Sign the executable first, then recalculate manifest executable.bytes/sha256 before packaging.' }));
  } catch (error) {
    console.error(`Archive helper build failed: ${error.message}`);
    process.exitCode = 1;
  }
}
