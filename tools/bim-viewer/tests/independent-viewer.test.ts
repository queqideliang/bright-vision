import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile, readdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { extractArchiveEntry } = require('../electron/archive-extractor.cjs');

test('distributed viewer has no authoring application launcher or SDK integration', async () => {
  for (const directory of ['electron', 'native']) {
    for (const file of await readdir(directory)) {
      if (!/\.(cjs|cs)$/u.test(file)) continue;
      const source = await readFile(path.join(directory, file), 'utf8');
      assert.doesNotMatch(source, /Revit\.exe|Autodesk\.Revit|bim:convert-rvt|revit-converter/iu, file);
    }
  }
  const manifest = JSON.parse(await readFile('package.json', 'utf8'));
  assert(!manifest.build.files.some((pattern: string) => pattern.startsWith('revit/')));
  assert.doesNotMatch(await readFile('electron/preload.cjs', 'utf8'), /convertRvt/u);
});

test('raw RVT archive requests are rejected before file access or helper launch', async () => {
  await assert.rejects(extractArchiveEntry({
    input: path.resolve('test-results/does-not-exist.zip'), index: 0, kind: 'rvt', name: 'original.rvt',
    root: path.resolve('test-results/unused'), helperDirectory: path.resolve('native/generated'),
    signal: new AbortController().signal,
  }), /RVT.*独立读取/u);
});
