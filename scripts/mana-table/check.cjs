const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

// Only the small shared CI toolset remains JavaScript. There is no app build,
// package install, renderer, browser automation or duplicate rules test runner.
function scripts(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name);
    return entry.isDirectory() ? scripts(file) : entry.name.endsWith('.cjs') ? [file] : [];
  });
}
const files = scripts(__dirname);
for (const file of files) {
  const result = spawnSync(process.execPath, ['--check', file], { stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) { process.exitCode = result.status || 1; break; }
}
if (!process.exitCode) console.log(`Syntax checked ${files.length} shared CI tool files.`);
