const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');

test('SUM imports preserve all three dual lands using available printings, while unknown names still block', { timeout: 60000 }, async () => {
  const engine = startEngine(testProfile('import-printings'));
  try {
    await ready(engine);
    const text = 'Deck\n1 Bayou (SUM) 283\u00a0\nSideboard\n2 Savannah (SUM) 285\n1 Scrubland (SUM) 286';
    const preview = await engine.request('importPreview', { text });
    assert.deepEqual(preview.problems, []);
    assert.deepEqual(preview.warnings.map(warning => warning.line), [2, 4, 5]);
    assert.deepEqual(preview.entries.map(entry => [entry.section, entry.card.name, entry.quantity]), [
      ['Main', 'Bayou', 1], ['Sideboard', 'Savannah', 2], ['Sideboard', 'Scrubland', 1]
    ]);
    const saved = await engine.request('import', { text, name: 'SUM regression', format: 'Constructed' });
    assert.equal(saved.deck.entries.reduce((sum, entry) => sum + entry.quantity, 0), 4);
    const exact = await engine.request('importPreview', { text: '1 Bayou (3ED) 283' });
    assert.deepEqual(exact.problems, []);
    assert.deepEqual(exact.warnings, []);
    assert.equal(exact.entries[0].card.edition, '3ED');
    const foil = await engine.request('importPreview', { text: 'SB: 2 Lightning Bolt (ZZZZZZ) 1 *F*' });
    assert.deepEqual(foil.problems, []);
    assert.equal(foil.entries[0].card.foil, true);
    assert.equal(foil.entries[0].section, 'Sideboard');
    assert.equal(foil.entries[0].quantity, 2);
    const bad = '1 Definitely Not A Real Card (SUM) 283';
    assert.equal((await engine.request('importPreview', { text: bad })).problems.length, 1);
    await assert.rejects(engine.request('import', { text: bad, name: 'Invalid' }));
    const native = await engine.request('importPreview', { text: '1 Bayou|SUM|1' });
    assert.equal(native.problems.length, 1, 'Native printing IDs must remain strict');
    assert.equal((await engine.request('snapshot')).id, saved.id);
  } finally { await engine.close(); }
});
