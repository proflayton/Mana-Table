const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { EngineClient } = require('../engine-client.cjs');
const { engineOptions } = require('../runtime.cjs');
const { testProfile, ready } = require('./support/engine.cjs');

test('a running development engine can load new classes after its build JAR is replaced', { timeout: 60000 }, async () => {
  const userData = testProfile('engine-rebuild');
  const options = engineOptions({ project: path.resolve(__dirname, '../..'), userData });
  const source = path.join(userData, 'rebuild-fixture.jar');
  fs.copyFileSync(options.jar, source);
  const engine = new EngineClient({ ...options, jar: source });
  try {
    await ready(engine);
    // Simulate Maven replacing its output. Only the test fixture is modified.
    fs.writeFileSync(source, 'a build is replacing this JAR');
    const preview = await engine.request('importPreview', { text: '1 Boreal Druid\n1 Fyndhorn Elves' });
    assert.deepEqual(preview.problems, []);
    assert.deepEqual(preview.entries.map(entry => entry.card.name), ['Boreal Druid', 'Fyndhorn Elves']);
    const imported = await engine.request('import', { text: '1 Boreal Druid\n1 Fyndhorn Elves', name: 'After rebuild' });
    assert.equal(imported.deck.entries.length, 2);
  } finally {
    await engine.close();
    fs.rmSync(source, { force: true });
  }
});
