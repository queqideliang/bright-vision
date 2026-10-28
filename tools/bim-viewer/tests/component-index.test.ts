import assert from 'node:assert/strict';
import test from 'node:test';
import { ComponentIndex, UNCLASSIFIED_CATEGORY, visibleTreeRows } from '../src/component-index.ts';
import type { IndexRecord } from '../src/component-index.ts';

const records: IndexRecord[] = [
  { id: 'a', name: 'A墙', category: '墙', family: '基本墙', type: '200', categoryCode: '-1', levels: ['一层', '一层'], triangles: 20 },
  { id: 'b', name: 'B窗', category: '窗', family: '平开窗', type: '900', categoryCode: '-2', levels: ['一层'], triangles: 40 },
  { id: 'c', name: 'C墙', category: '墙', family: '基本墙', type: '200', categoryCode: '-1', levels: ['二层'], triangles: 30 },
];

test('dimension postings intersect with cached case-insensitive search and do not duplicate level membership', () => {
  const index = new ComponentIndex(records);
  assert.deepEqual([...index.query({ search: '', level: '一层', category: '' }).indices].sort(), [0, 1]);
  assert.deepEqual([...index.query({ search: 'A墙', level: '一层', category: '墙' }).indices], [0]);
  assert.equal(index.query({ search: 'C墙', level: '一层', category: '墙' }).indices.length, 0);
  assert.deepEqual([...index.query({ search: 'b窗', level: '', category: '' }).indices], [1]);
});

test('hierarchy ranges select exactly the filtered descendant leaves and collapse skips whole subtrees', () => {
  const result = new ComponentIndex(records).query({ search: '', level: '', category: '' });
  const wall = result.groups.find(group => group.depth === 0 && group.label === '墙')!;
  assert.deepEqual([...result.indices.slice(wall.first, wall.last)].sort(), [0, 2]);
  const visible = visibleTreeRows(result, new Set([wall.key]));
  assert.ok(![...visible].includes(0)); assert.ok(![...visible].includes(2)); assert.ok([...visible].includes(1));
  assert.equal(result.categories.find(category => category.name === '墙')?.triangles, 50);
  assert.equal(result.heavy[0]?.index, 1);
});

test('unclassified category is a distinct filter rather than the all-categories option', () => {
  const index = new ComponentIndex([...records, { ...records[0]!, id: 'missing-category', category: '' }]);
  assert.deepEqual([...index.query({ search: '', level: '', category: UNCLASSIFIED_CATEGORY }).indices], [3]);
  assert.equal(index.query({ search: '', level: '', category: '' }).indices.length, 4);
});
