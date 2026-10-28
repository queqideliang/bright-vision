import assert from 'node:assert/strict';
import test from 'node:test';
import { compactView, expandView, readSavedViews } from '../src/saved-views.ts';
import type { ViewerViewpoint } from '../src/viewer/model-viewer.ts';

const view: ViewerViewpoint = { version: 1, position: [1, 2, 3], target: [0, 0, 0], visibleIds: ['a', 'b'],
  selectedIds: ['a'], ghostedIds: ['c'], ghostMode: true, sectionBox: { min: [0, 0, 0], max: [2, 2, 2] } };

test('saved viewpoint compacts visibility and faithfully restores camera, selection, ghost and section state', () => {
  const saved = compactView('检查层', view, ['a', 'b', 'c']);
  assert.deepEqual(saved.visibility, { mode: 'hidden', ids: ['c'] });
  const read = readSavedViews(JSON.stringify([saved])); assert.equal(read.length, 1);
  assert.deepEqual(expandView(read[0]!, ['a', 'b', 'c']), view);
  assert.deepEqual(expandView(read[0]!, ['b', 'c']).selectedIds, []);
});

test('malformed or oversized local saved data is ignored without executing or accepting non-finite geometry', () => {
  assert.deepEqual(readSavedViews('{bad'), []);
  const saved = compactView('valid', view, ['a', 'b', 'c']);
  assert.deepEqual(readSavedViews(JSON.stringify([{ ...saved, view: { ...saved.view, position: [null, 0, 0] } }])), []);
  assert.deepEqual(readSavedViews(JSON.stringify([{ ...saved, view: { ...saved.view, sectionBox: { min: [3, 0, 0], max: [2, 2, 2] } } }])), []);
});
