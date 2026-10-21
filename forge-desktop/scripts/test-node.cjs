const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const engine = new Set(['engine', 'engine-rebuild', 'match', 'commander', 'commander-benchmark', 'multiplayer', 'network-auto', 'network-mulligan', 'network-mana-choice', 'network-combat', 'pregame-concession', 'top-library', 'presets', 'response-skip', 'import-printings', 'sync-lobby']);
// Approximate observed seconds only balance work; they never choose coverage.
const seconds = { 'commander-benchmark': 75, commander: 21, 'engine-rebuild': 12,
  engine: 25, 'import-printings': 13, match: 18, multiplayer: 30, 'network-auto': 66,
  'network-combat': 98, 'network-mana-choice': 66, 'network-mulligan': 63,
  'pregame-concession': 55, presets: 14, 'response-skip': 30, 'sync-lobby': 116, 'top-library': 84 };
function discover(suite) {
  if (!['unit', 'engine'].includes(suite)) throw new Error('Choose unit or engine.');
  return fs.readdirSync(path.join(root, 'tests')).filter(file => file.endsWith('.test.cjs')
    && engine.has(file.replace('.test.cjs', '')) === (suite === 'engine')).sort();
}
function partitionEngine(files, count) {
  if (!Number.isInteger(count) || count < 1 || count > 64) throw new Error('Shard count must be between 1 and 64.');
  const work = files.flatMap(file => {
    const name = file.replace('.test.cjs', '');
    // These existing suites explicitly support one transport per invocation.
    const modes = ['commander-benchmark', 'pregame-concession'].includes(name) ? ['local', 'network', 'mixed'] : [null];
    return modes.map(mode => ({ file, mode, seconds: seconds[name] ?? 60 }));
  }).sort((a, b) => b.seconds - a.seconds || a.file.localeCompare(b.file) || String(a.mode).localeCompare(String(b.mode)));
  if (count > work.length) throw new Error('Every shard must contain work.');
  const shards = Array.from({ length: count }, () => ({ seconds: 0, work: [] }));
  for (const task of work) {
    const shard = shards.reduce((best, candidate) => candidate.seconds < best.seconds ? candidate : best);
    shard.work.push(task); shard.seconds += task.seconds;
  }
  return shards.map(shard => shard.work);
}
function parseShard(value) {
  const match = /^([1-9]\d*)\/([1-9]\d*)$/.exec(value);
  if (!match || +match[1] > +match[2] || +match[2] > 64) throw new Error('Use --shard=N/M with 1 <= N <= M <= 64.');
  return { index: +match[1] - 1, count: +match[2] };
}
function main(args) {
  const [suite, ...options] = args;
  const files = discover(suite);
  const shardOptions = options.filter(option => option.startsWith('--shard='));
  if (shardOptions.length > 1 || suite !== 'engine' && shardOptions.length) throw new Error('Use one engine shard option.');
  const forwarded = options.filter(option => !option.startsWith('--shard='));
  let work;
  if (shardOptions.length) {
    if (process.env.MANA_SYNC_MODE) throw new Error('Sharded runs select all transports; omit MANA_SYNC_MODE.');
    const shard = parseShard(shardOptions[0].slice('--shard='.length));
    work = partitionEngine(files, shard.count)[shard.index];
    console.log(`Engine shard ${shard.index + 1}/${shard.count}: ${work.map(task => task.file + (task.mode ? ':' + task.mode : '')).join(', ')}`);
  }
  const runs = work ?? [{ files }];
  for (const task of runs) {
    const selected = task.files ?? [task.file];
    const env = task.mode ? { ...process.env, MANA_SYNC_MODE: task.mode } : process.env;
    const result = spawnSync(process.execPath, ['--test', '--test-concurrency=1', ...forwarded, ...selected.map(file => path.join(root, 'tests', file))],
      { cwd: root, env, stdio: 'inherit', windowsHide: true });
    if (result.error) throw result.error;
    if (result.status !== 0) return result.status ?? 1;
  }
  return 0;
}
if (require.main === module) process.exitCode = main(process.argv.slice(2));
module.exports = { discover, partitionEngine, parseShard };
