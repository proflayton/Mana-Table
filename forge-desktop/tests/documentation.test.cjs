const test = require('node:test');
const assert = require('node:assert/strict');
const { checkDocuments, headingSlugs, links } = require('../scripts/check-docs.cjs');

function errors(source, files = {}) {
  const documents = { 'README.md': source, ...files };
  return checkDocuments(documents, target => Object.hasOwn(documents, target) ? { text: documents[target] } : undefined);
}
test('missing local files and headings report the source line', () => {
  assert.deepEqual(errors('# Start\n\n[Missing](gone.md)'), [{ file: 'README.md', line: 3, message: 'missing local target: gone.md' }]);
  assert.deepEqual(errors('[Missing](guide.md#gone)', { 'guide.md': '# Here' }), [{ file: 'README.md', line: 1, message: 'missing heading #gone in guide.md' }]);
});
test('URL-decoded paths and heading fragments resolve without network access', () => {
  assert.deepEqual(errors('[Guide](<guides/My%20Guide.md#hello%20world>)', { 'guides/My Guide.md': '<a id="hello world"></a>' }), []);
  assert.deepEqual(errors('[Guide](guides/My%20Guide.md#hello-world)', { 'guides/My Guide.md': '# Hello world' }), []);
});
test('fenced and inline code, images and external links are not checked as local links', () => {
  const source = '```md\n[Example](missing.md)\n```\n~~~\n[Example](also-missing.md)\n~~~\n`[Inline](no.md)`\n![Art](no.png)\n[Web](https://example.com/no.md)\n[Mail](mailto:test@example.com)';
  assert.deepEqual(errors(source), []);
  assert.deepEqual(links('``[Example](no.md) `inner` ``'), []);
});
test('GFM duplicate heading slugs, inline formatting and setext headings are supported', () => {
  const source = '# One\n# One\n# One-1\n# **Card** `rules` &amp; play!\nLast heading\n---\n# `MANA_USER_DATA_DIR`\n# _Emphasis_';
  assert.deepEqual([...headingSlugs(source)], ['one', 'one-1', 'one-1-1', 'card-rules--play', 'last-heading', 'mana_user_data_dir', 'emphasis']);
  assert.deepEqual(errors('[Duplicate](guide.md#one-1)', { 'guide.md': source }), []);
});
test('full, collapsed and shortcut reference links resolve definitions case-insensitively', () => {
  const source = '[Read][GUIDE]\n[Guide][]\n[guide]\n\n[guide]: guide.md#ready "Guide title"';
  assert.deepEqual(errors(source, { 'guide.md': '# Ready' }), []);
  assert.equal(errors(source).length, 3);
  assert.deepEqual(links('![image][picture]\n[picture]: missing.png'), []);
});
test('directories, repository-root links and non-Markdown line anchors retain their meaning', () => {
  const documents = { 'docs/README.md': '[Root](/README.md#home)\n[Directory](guide/)\n[Code](../main.java#L12)' };
  const targets = { 'README.md': { text: '# Home' }, 'docs/guide/': { directory: true }, 'main.java': { text: '' } };
  assert.deepEqual(checkDocuments(documents, target => targets[target]), []);
  assert.equal(errors('[Escape](../outside.md)')[0].message, 'link leaves the repository');
});
