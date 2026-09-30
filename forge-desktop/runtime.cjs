const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');

// Shared by the app, development checks and engine integration tests. Packaged
// builds use their bundled runtime unless FORGE_JAVA explicitly overrides it.
function resolveJava({ env = process.env, platform = process.platform, runtime } = {}) {
  if (env.FORGE_JAVA) return env.FORGE_JAVA;
  const home = runtime || env.JAVA_HOME;
  return home ? path.join(home, 'bin', platform === 'win32' ? 'java.exe' : 'java') : 'java';
}

function engineOptions({ project, userData, resourcesPath, env = process.env, platform = process.platform }) {
  return {
    java: resolveJava({ env, platform, runtime: resourcesPath && path.join(resourcesPath, 'runtime') }),
    jar: resourcesPath ? path.join(resourcesPath, 'forge-engine.jar') : path.join(project, 'forge-api', 'target', 'forge-engine.jar'),
    isolateJar: !resourcesPath,
    resources: resourcesPath ? path.join(resourcesPath, 'forge-res') : path.join(project, 'forge-gui', 'res'),
    data: path.join(userData, 'decks'),
    log: path.join(userData, 'engine.log')
  };
}

function resolveUserData({ env = process.env, packaged, executable, appData, sourceDirectory }) {
  if (env.MANA_USER_DATA_DIR || env.FORGE_USER_DATA) return env.MANA_USER_DATA_DIR || env.FORGE_USER_DATA;
  if (!packaged) return path.join(sourceDirectory, '.data');
  // Preserve older portable builds. Clean releases share a stable profile, so
  // extracting a newer release to a different directory keeps decks/settings.
  const portable = path.join(path.dirname(executable), 'UserData');
  return fs.existsSync(portable) ? portable : path.join(appData, 'Mana Table');
}

// Java loads classes lazily. Maven can replace target/forge-engine.jar while a
// development game is running, so each engine needs its own immutable copy.
// Versioned packaged builds already have a separate JAR and do not need this.
function snapshotEngineJar(source) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-table-engine-'));
  const jar = path.join(directory, 'forge-engine.jar');
  const dispose = () => fs.rmSync(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
  try { fs.copyFileSync(source, jar); }
  catch (error) { dispose(); throw error; }
  return { jar, dispose };
}

module.exports = { resolveJava, engineOptions, snapshotEngineJar, resolveUserData };
