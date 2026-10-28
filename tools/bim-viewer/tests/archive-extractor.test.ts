import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, writeFile, readdir, realpath, rm, unlink, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { zipSync, unzipSync, strToU8 } from 'fflate';
// Build tests inject a JavaScript compiler double; no compiler or EXE runs.
// @ts-ignore -- this build-only script is intentionally JavaScript.
import { buildNativeHelper, assemblyVersion } from '../scripts/build-native.mjs';

const modulePath = fileURLToPath(new URL('../electron/archive-extractor.cjs', import.meta.url));
const require = createRequire(modulePath);
const version = require('../package.json').version;
const sha = (value: Uint8Array) => createHash('sha256').update(value).digest('hex');

async function temporary(t: any) {
  const parent = await realpath(tmpdir());
  const directory = await mkdtemp(path.join(parent, 'bright-archive-mock-'));
  t.after(async () => {
    assert.equal(path.dirname(directory), parent);
    await rm(directory, { recursive: true, force: true });
  });
  return directory;
}

async function harness(t: any, behavior: any = {}) {
  const directory = await temporary(t);
  const helperDirectory = path.join(directory, 'resources', 'native-helper');
  const root = path.join(directory, 'cache');
  await mkdir(helperDirectory, { recursive: true });
  // These are inert text bytes, not a PE executable. The process double below
  // receives the path; Node never executes these files or starts a child.
  const payload = Buffer.from('INERT MOCK HELPER, NEVER EXECUTED');
  const executable = path.join(helperDirectory, 'ArchiveExtractor.exe');
  await writeFile(executable, payload);
  const manifest: any = { schemaVersion: 1, component: 'archive-extractor', productVersion: version,
    source: { name: 'ArchiveExtractor.cs', bytes: 32, sha256: 'a'.repeat(64) },
    compiler: { path: path.resolve(directory, 'never-run-csc.exe'), bytes: 32, sha256: 'b'.repeat(64) },
    framework: '.NETFramework,Version=v4.5', executable: { name: 'ArchiveExtractor.exe', bytes: payload.length, sha256: sha(payload) } };
  const manifestPath = path.join(helperDirectory, 'manifest.json');
  await writeFile(manifestPath, JSON.stringify(manifest));
  const inner = zipSync({ 'model.json': strToU8('{"components":[]}') });
  const input = path.join(directory, 'outer.zip');
  await writeFile(input, zipSync({ 'folder/model.zip': inner }));
  const launches: any[] = [];
  const execFile = (file: string, args: string[], options: any, callback: any) => {
    launches.push({ file, args, options });
    const task = async () => {
      if (behavior.launch) return await behavior.launch({ file, args, options });
      const request = JSON.parse(await readFile(args[0]!, 'utf8'));
      const nested = Object.values(unzipSync(await readFile(request.input)))[request.index]!;
      await writeFile(request.output, nested);
    };
    task().then(() => callback(null, '', ''), (error: any) => callback(error, '', error.stderr || ''));
    return {};
  };
  const fs = require('node:fs/promises');
  const reads: string[] = [];
  const context = vm.createContext({ Buffer });
  const wrapper = new vm.Script(`(function(require,module,exports){\n${await readFile(modulePath, 'utf8')}\n})`, { filename: modulePath }).runInContext(context);
  const loaded: any = { exports: {} };
  wrapper((name: string) => name === 'node:child_process' ? { execFile } : name === 'node:fs/promises' ? {
    ...fs,
    readFile: (...args: any[]) => { reads.push(args[0]); return fs.readFile(...args); },
    stat: async (file: string) => behavior.stat ? behavior.stat(file, fs.stat) : fs.stat(file),
  } : require(name), loaded, loaded.exports);
  return { ...loaded.exports, directory, helperDirectory, root, executable, manifest, manifestPath, input, inner, payload, launches, reads,
    call: (extra: any = {}) => loaded.exports.extractArchiveEntry({ input, index: 0, kind: 'zip', name: 'folder/model.zip', root, helperDirectory, ...extra }),
    saveManifest: () => writeFile(manifestPath, JSON.stringify(manifest)),
  };
}

test('fixed helper extracts a synthetic nested ZIP through a process double; cache contains only data', async t => {
  const h = await harness(t);
  const result = await h.call();
  assert.deepEqual(await readFile(result.path), Buffer.from(h.inner));
  assert.equal(result.name, 'model.zip');
  assert.equal(result.size, h.inner.length);
  assert.equal(h.launches.length, 1);
  assert.equal(h.launches[0].file, h.executable);
  assert.equal(h.launches[0].options.windowsHide, true);
  assert.equal(h.launches[0].options.signal, undefined);
  assert.deepEqual((await readdir(path.dirname(result.path))).sort(), ['extract.json', 'model.zip']);
  assert(h.reads.every((file: string) => !file.endsWith('.cs')));
  assert(h.launches.every((launch: any) => path.basename(launch.file) === 'ArchiveExtractor.exe'));
});

test('null signal is accepted and missing helper fails without compiling or creating cache', async t => {
  const h = await harness(t);
  await h.call({ signal: null });
  await unlink(h.executable);
  await assert.rejects(h.call(), /ENOENT/);
  assert.equal(h.launches.length, 1);
  assert.equal((await readdir(h.root)).length, 1);
});

test('same-size changed helper fails hash verification before launching', async t => {
  const h = await harness(t);
  await writeFile(h.executable, Buffer.alloc(h.payload.length, 65));
  await assert.rejects(h.call(), /完整性/);
  assert.equal(h.launches.length, 0);
});

test('changed size and over-limit manifest/helper are rejected', async t => {
  const h = await harness(t);
  await writeFile(h.executable, 'short');
  await assert.rejects(h.call(), /大小/);
  await writeFile(h.executable, h.payload);
  h.manifest.executable.bytes = 16 * 1024 * 1024 + 1;
  await h.saveManifest();
  await assert.rejects(h.call(), /不匹配/);
  await writeFile(h.manifestPath, Buffer.alloc(65537, 32));
  await assert.rejects(h.call(), /清单大小/);
  assert.equal(h.launches.length, 0);
});

test('malformed, different-version, path-injected, or invalid-source manifests never launch', async t => {
  const h = await harness(t);
  await writeFile(h.manifestPath, '{');
  await assert.rejects(h.call(), /清单格式/);
  for (const change of [(m: any) => { m.productVersion = '0.0.0'; }, (m: any) => { m.executable.name = '../other.exe'; },
    (m: any) => { m.source.sha256 = 'bad'; }, (m: any) => { m.compiler.path = 'relative.exe'; }]) {
    const good = JSON.stringify(h.manifest);
    change(h.manifest); await h.saveManifest();
    await assert.rejects(h.call(), /不匹配/);
    Object.assign(h.manifest, JSON.parse(good));
  }
  assert.equal(h.launches.length, 0);
});

test('raw RVT, invalid indices and overlapping/relative directories fail before launch', async t => {
  const h = await harness(t);
  await assert.rejects(h.call({ kind: 'rvt', helperDirectory: undefined }), /RVT.*独立读取/u);
  for (const index of [-1, 1.5, 1000000, NaN]) await assert.rejects(h.call({ index }), /选择无效/);
  await assert.rejects(h.call({ root: h.helperDirectory }), /分开安装/);
  await assert.rejects(h.call({ helperDirectory: path.join(h.root, 'inside') }), /分开安装/);
  await assert.rejects(h.call({ helperDirectory: 'relative' }), /目录无效/);
  assert.equal(h.launches.length, 0);
});

test('pre-cancel and in-flight cancellation are safe and remove only request data', async t => {
  const controller = new AbortController();
  const h = await harness(t, { launch: async ({ args, options }: any) => {
    const request = JSON.parse(await readFile(args[0], 'utf8'));
    await writeFile(request.output + '.tmp', 'partial');
    assert.equal(options.signal, controller.signal);
    controller.abort();
    throw Object.assign(new Error('aborted'), { code: 'ABORT_ERR' });
  } });
  const pre = new AbortController(); pre.abort();
  await assert.rejects(h.call({ signal: pre.signal }), /取消/);
  assert.equal(h.launches.length, 0);
  await assert.rejects(h.call({ signal: controller.signal }), /取消/);
  assert.equal(h.launches.length, 1);
  assert.deepEqual(await readdir(h.root), []);
  assert.deepEqual(await readFile(h.executable), h.payload);
});

test('outer and extracted model size limits are enforced without giant files', async t => {
  let oversizedInput = true;
  const h = await harness(t, { stat: async (file: string, actual: any) => {
    if (file.endsWith('outer.zip') && oversizedInput) return { isFile: () => true, size: 32 * 1024 ** 3 + 1 };
    if (file.endsWith('model.zip')) return { isFile: () => true, size: 2 * 1024 ** 3 + 1 };
    return actual(file);
  } });
  await assert.rejects(h.call(), /模型包大小/);
  assert.equal(h.launches.length, 0);
  oversizedInput = false;
  await assert.rejects(h.call(), /模型文件大小/);
  assert.deepEqual(await readdir(h.root), []);
});

test('resource junctions are rejected, including redirected parent directories', async t => {
  const h = await harness(t);
  const alias = path.join(h.directory, 'linked-resources');
  await symlink(path.dirname(h.helperDirectory), alias, process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(h.call({ helperDirectory: path.join(alias, 'native-helper') }), /链接|联接/);
  assert.equal(h.launches.length, 0);
});

test('helper exit failure is surfaced once without regeneration or another loader', async t => {
  const h = await harness(t, { launch: async () => { throw Object.assign(new Error('process failed'), { stderr: 'synthetic exit 1' }); } });
  await assert.rejects(h.call(), /synthetic exit 1/);
  assert.equal(h.launches.length, 1);
  assert.deepEqual(await readdir(h.root), []);
});

test('build script emits version metadata and manifest using an inert compiler double, then reuses unchanged output', async t => {
  const projectDirectory = await temporary(t);
  await mkdir(path.join(projectDirectory, 'native'));
  await writeFile(path.join(projectDirectory, 'package.json'), JSON.stringify({ version: '0.5.2' }));
  await writeFile(path.join(projectDirectory, 'native', 'ArchiveExtractor.cs'), '// synthetic source, never compiled');
  const compilerPath = path.join(projectDirectory, 'mock-compiler.dat');
  await writeFile(compilerPath, 'INERT COMPILER IDENTITY, NEVER RUN');
  let calls = 0;
  const executeCompiler = async (file: string, args: string[], options: any) => {
    assert.equal(file, compilerPath); assert.equal(options.windowsHide, true); calls++;
    const metadata = await readFile(args.at(-1)!, 'utf8');
    assert.match(metadata, /AssemblyFileVersion\("0\.5\.2\.0"\)/);
    assert.match(metadata, /AssemblyInformationalVersion\("0\.5\.2"\)/);
    const output = args.find(value => value.startsWith('/out:'))!.slice(5);
    await writeFile(output, 'INERT MOCK OUTPUT, NOT A PE, NEVER EXECUTED');
  };
  const options = { projectDirectory, compilerPath, executeCompiler, platform: 'win32' };
  const first = await buildNativeHelper(options);
  assert.equal(first.reused, false); assert.equal(calls, 1);
  assert.equal(first.manifest.productVersion, '0.5.2');
  assert.equal(first.manifest.executable.name, 'ArchiveExtractor.exe');
  assert.equal(first.manifest.executable.sha256, sha(await readFile(path.join(first.directory, 'ArchiveExtractor.exe'))));
  assert.match(first.manifest.integrityNote, /not publisher trust/);
  assert.deepEqual((await readdir(first.directory)).sort(), ['ArchiveExtractor.exe', 'manifest.json']);
  const second = await buildNativeHelper(options);
  assert.equal(second.reused, true); assert.equal(calls, 1);
  await writeFile(path.join(projectDirectory, 'native', 'ArchiveExtractor.cs'), '// changed source');
  await buildNativeHelper(options); assert.equal(calls, 2);
  const generatedExecutable = path.join(first.directory, 'ArchiveExtractor.exe');
  const goodBytes = await readFile(generatedExecutable);
  await writeFile(generatedExecutable, Buffer.alloc(goodBytes.length, 65));
  await assert.rejects(buildNativeHelper(options), /integrity.*no rebuild/i);
  assert.equal(calls, 2);
  await writeFile(generatedExecutable, goodBytes);
  await unlink(generatedExecutable);
  await assert.rejects(buildNativeHelper(options), /missing.*no rebuild/i);
  assert.equal(calls, 2);
  await writeFile(generatedExecutable, goodBytes);
  await writeFile(path.join(first.directory, 'manifest.json'), 'null');
  await assert.rejects(buildNativeHelper(options), /invalid schema/);
  assert.equal(calls, 2);
  await assert.rejects(buildNativeHelper({ ...options, platform: 'linux' }), /only on Windows/);
  assert.equal(calls, 2);
});

test('mock compiler failure removes its private build data and never installs a helper', async t => {
  const projectDirectory = await temporary(t);
  await mkdir(path.join(projectDirectory, 'native'));
  await writeFile(path.join(projectDirectory, 'package.json'), JSON.stringify({ version: '0.5.2' }));
  await writeFile(path.join(projectDirectory, 'native', 'ArchiveExtractor.cs'), '// not compiled');
  const compilerPath = path.join(projectDirectory, 'mock-compiler.dat');
  await writeFile(compilerPath, 'NOT A COMPILER');
  let calls = 0;
  await assert.rejects(buildNativeHelper({ projectDirectory, compilerPath, platform: 'win32', executeCompiler: async () => {
    calls++; throw new Error('synthetic compiler blocked');
  } }), /synthetic compiler blocked/);
  assert.equal(calls, 1);
  assert.deepEqual(await readdir(path.join(projectDirectory, 'native', 'generated')), []);
});

test('version conversion refuses injectable or unrepresentable assembly metadata', () => {
  assert.equal(assemblyVersion('0.5.2'), '0.5.2.0');
  assert.equal(assemblyVersion('0.5.2-rc.1'), '0.5.2.0');
  for (const version of ['0.5', '0.5.2\"', '65535.1.0', '0.5.2\n']) assert.throws(() => assemblyVersion(version));
});
