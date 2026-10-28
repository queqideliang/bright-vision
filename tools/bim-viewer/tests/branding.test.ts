import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { brand, brandPresentation } from '../src/brand.ts';

test('official company attribution survives both interface languages', () => {
  for (const language of ['zh', 'en'] as const) {
    const presentation = brandPresentation(language);
    assert.ok(presentation.title.includes(brand.company));
    assert.ok(presentation.copyright.includes(brand.company));
    assert.ok(presentation.copyright.includes('2026'));
    assert.ok(presentation.licenseSummary.includes('Apache-2.0'));
    assert.equal(presentation.name, 'Bright Vision');
  }
  assert.match(brandPresentation('en').subtitle, /Local BIM workspace/);
  assert.doesNotMatch(brandPresentation('en').subtitle, /[\u3400-\u9fff]/);
  assert.ok(brandPresentation('zh').subtitle.includes(brand.company));
  assert.match(brandPresentation('zh').subtitle, /本地BIM工作台/);
});

test('displayed product version matches package metadata and static document identifies the company', async () => {
  const metadata = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8')) as { version: string };
  assert.equal(brand.version, metadata.version);
  const html = await readFile(new URL('../index.html', import.meta.url), 'utf8');
  assert.ok(html.includes(brand.company));
  assert.ok(html.includes('<meta name="author"'));
  assert.equal(brand.monogram, 'BV');
});
