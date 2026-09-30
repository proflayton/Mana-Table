const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { resolveJava, engineOptions, snapshotEngineJar } = require('../runtime.cjs');

test('Java discovery supports contributor machines and preserves bundled-runtime precedence', () => {
  for (const platform of ['win32', 'linux', 'darwin']) {
    const java = platform === 'win32' ? 'java.exe' : 'java';
    assert.equal(resolveJava({ env: {}, platform }), 'java');
    assert.equal(resolveJava({ env: { JAVA_HOME: '/jdk' }, platform }), path.join('/jdk', 'bin', java));
    const options = engineOptions({ project: '/checkout', userData: '/profile', resourcesPath: '/bundle',
      env: { JAVA_HOME: '/system-jdk' }, platform });
    assert.equal(options.java, path.join('/bundle', 'runtime', 'bin', java));
    assert.equal(options.jar, path.join('/bundle', 'forge-engine.jar'));
    assert.equal(options.isolateJar, false);
    assert.equal(options.data, path.join('/profile', 'decks'));
    assert.equal(resolveJava({ env: { FORGE_JAVA: '/custom/java', JAVA_HOME: '/jdk' }, runtime: '/bundle', platform }), '/custom/java');
  }
  const dev = engineOptions({ project: '/checkout', userData: '/profile', env: {}, platform: 'linux' });
  assert.equal(dev.jar, path.join('/checkout', 'forge-api', 'target', 'forge-engine.jar'));
  assert.equal(dev.resources, path.join('/checkout', 'forge-gui', 'res'));
  assert.equal(dev.isolateJar, true);
});

test('running engines retain their own JAR when a development rebuild replaces the source', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-table-runtime-test-'));
  const source = path.join(directory, 'forge-engine.jar');
  let first, second;
  try {
    fs.writeFileSync(source, 'engine version one');
    first = snapshotEngineJar(source);
    fs.rmSync(source);
    fs.writeFileSync(source, 'engine version two');
    second = snapshotEngineJar(source);
    assert.notEqual(first.jar, second.jar);
    assert.equal(fs.readFileSync(first.jar, 'utf8'), 'engine version one');
    assert.equal(fs.readFileSync(second.jar, 'utf8'), 'engine version two');
    first.dispose();
    assert.equal(fs.existsSync(first.jar), false);
    assert.equal(fs.readFileSync(second.jar, 'utf8'), 'engine version two');
    assert.equal(fs.readFileSync(source, 'utf8'), 'engine version two');
  } finally {
    first?.dispose();
    second?.dispose();
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
