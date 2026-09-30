const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const tools = path.join(root, '.tools');
const appSource = path.join(root, 'forge-desktop');
const release = process.argv.includes('--release');
if (process.argv.slice(2).some(arg => arg !== '--release')) throw new Error('Only --release is supported.');
const manifestPath = path.join(root, 'dist', release ? 'latest-release.json' : 'latest-beta.json');
const previousBeta = !release && fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, 'utf8').replace(/^\uFEFF/, '')) : null;
const stamp = new Date().toISOString().replace(/[-:TZ.]/g, '').slice(0, 14);
const build = path.join(tools, `desktop-beta-${stamp}`);
const stage = path.join(build, 'app');
const resources = path.join(build, 'forge-res');
const runtime = path.join(build, 'runtime');
const jar = path.join(root, 'forge-api/target/forge-engine.jar');
const javaHome = process.env.JAVA_HOME;
const revision = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
if (revision.status !== 0) throw new Error('Could not determine the source revision for this package.');
const sourceRevision = revision.stdout.trim();
if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('The package script currently supports Windows x64 only.');
if (!javaHome || !fs.existsSync(path.join(javaHome, 'bin', 'jlink.exe'))) throw new Error('Set JAVA_HOME to a JDK 17+ installation containing bin/jlink.exe.');
if (!fs.existsSync(jar)) throw new Error('Build forge-api with Maven before packaging.');
fs.mkdirSync(stage, { recursive: true });
for (const file of ['main.cjs', 'preload.cjs', 'engine-client.cjs', 'runtime.cjs', 'deck-sources.cjs', 'preferences.cjs', 'renderer']) {
  fs.cpSync(path.join(appSource, file), path.join(stage, file), { recursive: true });
}
const metadata = JSON.parse(fs.readFileSync(path.join(appSource, 'package.json'), 'utf8'));
const three = path.join(appSource, 'node_modules', 'three');
const sceneVendor = path.join(stage, 'vendor', 'three');
fs.mkdirSync(sceneVendor, { recursive: true });
for (const file of ['three.module.js', 'three.core.js']) fs.copyFileSync(path.join(three, 'build', file), path.join(sceneVendor, file));
fs.copyFileSync(path.join(three, 'LICENSE'), path.join(sceneVendor, 'LICENSE'));
delete metadata.devDependencies;
delete metadata.scripts;
fs.writeFileSync(path.join(stage, 'package.json'), JSON.stringify(metadata, null, 2));
fs.copyFileSync(path.join(root, 'LICENSE'), path.join(stage, 'LICENSE'));
console.log('Collecting Forge card resources…');
for (const folder of ['cardsfolder', 'tokenscripts', 'editions', 'languages', 'blockdata', 'lists', 'setlookup', 'ai']) {
  fs.cpSync(path.join(root, 'forge-gui/res', folder), path.join(resources, folder), { recursive: true });
}
console.log('Building the bundled Java runtime…');
const result = spawnSync(path.join(javaHome, 'bin/jlink.exe'), ['--add-modules', 'java.se,jdk.unsupported,jdk.crypto.ec',
  '--strip-debug', '--no-header-files', '--no-man-pages', '--compress=2', '--output', runtime], { stdio: 'inherit', windowsHide: true });
if (result.status !== 0) throw new Error('Could not build the Java runtime.');
(async () => {
  const { packager } = await import('@electron/packager');
  const output = path.join(root, 'dist', `${metadata.productName.replace(/\s+/g, '')}-${metadata.version}-${release ? 'release-' : ''}${stamp}`);
  const executable = `${metadata.productName}.exe`;
  const packages = await packager({
    dir: stage, name: metadata.productName, platform: 'win32', arch: 'x64', out: output, overwrite: false,
    asar: true, prune: false, electronVersion: require('electron/package.json').version,
    extraResource: [jar, resources, runtime],
    win32metadata: { CompanyName: 'proflayton', FileDescription: `${metadata.productName} desktop beta`, ProductName: metadata.productName }
  });
  for (const packaged of packages) {
    fs.writeFileSync(path.join(packaged, 'Launch Mana Table.cmd'), '@echo off\r\nsetlocal\r\nset ELECTRON_RUN_AS_NODE=\r\ncd /d "%~dp0"\r\nstart "" "%~dp0Mana Table.exe"\r\n');
    fs.copyFileSync(path.join(root, 'LICENSE'), path.join(packaged, 'FORGE-LICENSE.txt'));
    fs.copyFileSync(path.join(appSource, 'BETA.md'), path.join(packaged, 'START-HERE.md'));
    fs.writeFileSync(path.join(packaged, 'SOURCE.txt'), `Source: https://github.com/proflayton/Mana-Table/tree/${sourceRevision}\nVersion: ${metadata.version}\nEngine SHA-256: ${createHash('sha256').update(fs.readFileSync(jar)).digest('hex')}\nForge upstream: https://github.com/Card-Forge/forge\nForge is GPL-3.0-or-later.\nElectron and Java notices accompany their bundled runtimes.\n`);
    if (previousBeta) {
      const previousPreferences = path.join(previousBeta.directory, 'UserData', 'preferences.json');
      if (fs.existsSync(previousPreferences)) {
        const destination = path.join(packaged, 'UserData', 'preferences.json');
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.copyFileSync(previousPreferences, destination, fs.constants.COPYFILE_EXCL);
        if (!fs.readFileSync(previousPreferences).equals(fs.readFileSync(destination))) throw new Error('Play preference verification failed');
        console.log('Preserved and verified play preferences from the previous beta.');
      }
      for (const folder of ['decks', 'art']) {
        const previous = path.join(previousBeta.directory, 'UserData', folder);
        const destination = path.join(packaged, 'UserData', folder);
        if (fs.existsSync(previous)) fs.cpSync(previous, destination, { recursive: true, errorOnExist: true, force: false });
        if (folder === 'decks' && fs.existsSync(previous)) {
          const files = fs.readdirSync(previous).filter(file => file.endsWith('.json'));
          for (const file of files) {
            const digest = directory => createHash('sha256').update(fs.readFileSync(path.join(directory, file))).digest('hex');
            if (digest(previous) !== digest(destination)) throw new Error(`Saved deck verification failed: ${file}`);
          }
          console.log(`Preserved and verified ${files.length} saved deck files from the previous beta.`);
        }
      }
    }
    console.log(`BETA_READY=${path.join(packaged, executable)}`);
  }
  let archive, checksum;
  if (release) {
    const { archiveRelease } = require('./release-artifacts.cjs');
    ({ archive, checksum } = archiveRelease({ directory: packages[0], output, version: metadata.version }));
  }
  fs.writeFileSync(manifestPath, JSON.stringify({ version: metadata.version, directory: packages[0], executable, sourceRevision,
    ...(release ? { archive, checksum } : {}) }, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
