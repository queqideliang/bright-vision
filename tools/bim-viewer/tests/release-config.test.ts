import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';

const require = createRequire(import.meta.url);
const project = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const release = require('../electron-builder.release.cjs');
const updateSource = JSON.parse(await readFile(new URL('../electron/update-release.json', import.meta.url), 'utf8'));

test('official preview uses only the user-confirmed fixed GitHub repository', () => {
  assert.deepEqual(updateSource, { schemaVersion: 1, enabled: true, owner: 'queqideliang', repository: 'bright-vision', channel: 'preview', productId: 'local.bim.modelviewer' });
});

test('formal release requires signing and edits metadata without changing the unsigned preview policy', () => {
  assert.equal(project.version, '0.6.0');
  assert.equal(project.author, '崛亮视觉');
  assert.equal(project.build.win.signAndEditExecutable, true);
  assert.equal(project.build.win.signExecutable, false);
  assert.equal(release.win.signAndEditExecutable, true);
  assert.equal(release.win.signExecutable, true);
  assert.equal(release.forceCodeSigning, true);
  assert.equal(release.directories.output, 'release/signed');
  assert.equal(release.publish, null);
  assert.equal(release.appId, project.build.appId);
  assert.equal(release.nsis.guid, project.build.nsis.guid);
  assert.equal(release.win.certificateFile, undefined);
  assert.equal(release.win.signtoolOptions, undefined);
  assert.equal(release.win.azureSignOptions, undefined);
});

test('native helper compilation is explicit on Windows paths and never required by the pure Web build', () => {
  assert.equal(project.scripts['build:native'], 'node scripts/build-native.mjs');
  assert.doesNotMatch(project.scripts.build, /build:native|build-native|csc/i);
  for (const name of ['desktop', 'pack:dir', 'dist:win', 'dist:win:release']) {
    assert.match(project.scripts[name], /npm run build:native/);
  }
  assert.match(project.scripts['dist:win:release'], /--config electron-builder\.release\.cjs.*--publish never/);
  assert.deepEqual(project.build.extraResources.find((entry: { to: string }) => entry.to === 'native-helper'), {
    from: 'native/generated', to: 'native-helper',
  });
  assert.ok(project.build.files.includes('native/**/*.cs'));
  assert.ok(!project.build.files.includes('native/**/*'));
  assert.deepEqual(release.extraResources, project.build.extraResources);
});
