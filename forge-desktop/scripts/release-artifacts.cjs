const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createHash } = require('node:crypto');

function assertCleanPackage(directory) {
  const forbidden = new Set(['userdata', '.data', '.data-host', '.data-second', '.data-third', 'test-results', 'engine.log', 'desktop.log']);
  function visit(folder) {
    for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
      if (forbidden.has(entry.name.toLowerCase())) throw new Error(`Player data is not allowed in releases: ${entry.name}`);
      if (entry.isSymbolicLink()) throw new Error(`Release packages must not contain links: ${entry.name}`);
      if (entry.isDirectory()) visit(path.join(folder, entry.name));
    }
  }
  visit(directory);
}

function archiveRelease({ directory, output, version }) {
  assertCleanPackage(directory);
  const archive = path.join(output, `ManaTable-${version}-windows-x64.zip`);
  const checksum = `${archive}.sha256`;
  if (fs.existsSync(archive)) throw new Error('Refusing to replace an existing release archive.');
  // Windows ships bsdtar; argument arrays preserve spaces without shell quoting.
  const result = spawnSync('tar.exe', ['-a', '-c', '-f', archive, '-C', path.dirname(directory), path.basename(directory)],
    { stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Could not create the release ZIP.');
  const hash = createHash('sha256');
  const descriptor = fs.openSync(archive, 'r');
  try {
    const buffer = Buffer.alloc(1024 * 1024);
    let count;
    while ((count = fs.readSync(descriptor, buffer, 0, buffer.length, null)) > 0) hash.update(buffer.subarray(0, count));
  } finally { fs.closeSync(descriptor); }
  fs.writeFileSync(checksum, `${hash.digest('hex')}  ${path.basename(archive)}\n`);
  return { archive, checksum };
}

module.exports = { assertCleanPackage, archiveRelease };
