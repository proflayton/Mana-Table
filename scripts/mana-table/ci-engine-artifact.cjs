const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

function requireRevision(value) {
  if (!/^[a-f0-9]{40}$/.test(value ?? '')) throw new Error('A full lowercase source revision is required.');
}
async function identity(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size === 0) throw new Error('Engine artifact must be a nonempty regular file.');
  const hash = crypto.createHash('sha256');
  for await (const chunk of fs.createReadStream(file)) hash.update(chunk);
  return { bytes: stat.size, sha256: hash.digest('hex') };
}
async function create(jar, fixtureLauncher, manifest, revision, head) {
  requireRevision(revision);
  if (head !== revision) throw new Error('Checkout does not match the expected source revision.');
  const record = { schemaVersion: 2, sourceRevision: revision, engine: await identity(jar), fixtureLauncher: await identity(fixtureLauncher) };
  fs.writeFileSync(manifest, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
  return record;
}
async function verify(jar, fixtureLauncher, manifest, revision, head) {
  requireRevision(revision);
  if (head !== revision) throw new Error('Checkout does not match the expected source revision.');
  const stat = fs.lstatSync(manifest);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16384) throw new Error('Invalid engine artifact manifest.');
  const record = JSON.parse(fs.readFileSync(manifest, 'utf8'));
  if (record.schemaVersion !== 2 || record.sourceRevision !== revision) throw new Error('Engine artifact belongs to another source revision or verification schema.');
  const actual = await identity(jar);
  if (record.engine?.bytes !== actual.bytes || record.engine?.sha256 !== actual.sha256) throw new Error('Engine artifact bytes differ from the verified build.');
  const actualLauncher = await identity(fixtureLauncher);
  if (record.fixtureLauncher?.bytes !== actualLauncher.bytes || record.fixtureLauncher?.sha256 !== actualLauncher.sha256)
    throw new Error('Fixture launcher bytes differ from the verified build.');
  return record;
}
async function main(args) {
  const [operation, revision = process.env.GITHUB_SHA] = args;
  if (args.length > 2 || !['create', 'verify'].includes(operation)) throw new Error('Use create|verify [expected-source-sha].');
  const repository = path.resolve(__dirname, '../..');
  const head = execFileSync('git', ['-c', 'core.excludesfile=', 'rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8', windowsHide: true }).trim();
  if (operation === 'create' && execFileSync('git', ['-c', 'core.excludesfile=', 'status', '--porcelain', '--untracked-files=all'],
    { cwd: repository, encoding: 'utf8', windowsHide: true }).trim()) throw new Error('Engine artifacts must be produced from a clean source checkout.');
  const target = path.join(repository, 'forge-api/target');
  const record = await ({ create, verify }[operation])(path.join(target, 'forge-engine.jar'), path.join(target, 'forge-engine-fixtures.jar'),
    path.join(target, 'ci-engine-manifest.json'), revision, head);
  console.log(`Engine ${operation}: ${record.sourceRevision} / ${record.engine.sha256} / ${record.engine.bytes} bytes; fixture launcher ${record.fixtureLauncher.sha256}`);
}
if (require.main === module) main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { create, verify };
