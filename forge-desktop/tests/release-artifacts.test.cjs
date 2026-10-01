const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { assertCleanPackage } = require('../scripts/release-artifacts.cjs');

test('release archives reject saved profiles and diagnostics before creating an uploadable ZIP', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-table-release-test-'));
  try {
    fs.mkdirSync(path.join(directory, 'resources'));
    fs.writeFileSync(path.join(directory, 'resources', 'forge-engine.jar'), 'bundled engine');
    assert.doesNotThrow(() => assertCleanPackage(directory));
    fs.mkdirSync(path.join(directory, 'UserData'));
    assert.throws(() => assertCleanPackage(directory), /Player data/);
    fs.rmdirSync(path.join(directory, 'UserData'));
    fs.writeFileSync(path.join(directory, 'resources', 'engine.log'), 'private diagnostics');
    assert.throws(() => assertCleanPackage(directory), /Player data/);
  } finally { fs.rmSync(directory, { recursive: true, force: true }); }
});
