const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { discover, partitionEngine, parseShard } = require('../scripts/test-node.cjs');

test('parallel engine shards cover every discovered suite and every supported transport', () => {
  const files = discover('engine');
  for (const count of [1, 3, 8, 12]) {
    const shards = partitionEngine(files, count);
    assert.equal(shards.length, count);
    assert.ok(shards.every(shard => shard.length));
    const all = shards.flat();
    assert.equal(new Set(all.map(task => `${task.file}:${task.mode}`)).size, all.length, 'No duplicated work across shards');
    assert.deepEqual([...new Set(all.map(task => task.file))].sort(), files, 'No omitted files');
    for (const file of files) {
      const modes = all.filter(task => task.file === file).map(task => task.mode).sort();
      assert.deepEqual(modes, ['commander-benchmark.test.cjs', 'pregame-concession.test.cjs'].includes(file)
        ? ['local', 'mixed', 'network'] : [null]);
    }
    assert.deepEqual(partitionEngine([...files].reverse(), count), shards, 'Discovery order cannot change coverage');
  }
});
test('invalid or empty shard selections fail instead of reporting a false pass', () => {
  for (const value of ['0/12', '13/12', '1/0', '1/65', '1', '1/12junk', '-1/12']) assert.throws(() => parseShard(value));
  assert.deepEqual(parseShard('12/12'), { index: 11, count: 12 });
  assert.throws(() => partitionEngine(['engine.test.cjs'], 2), /contain work/);
});
test('the shard CLI rejects inherited transport filters and propagates a failed child', () => {
  const runner = path.resolve(__dirname, '../scripts/test-node.cjs');
  const filtered = spawnSync(process.execPath, [runner, 'engine', '--shard=1/8'],
    { env: { ...process.env, MANA_SYNC_MODE: 'local' }, encoding: 'utf8', windowsHide: true });
  assert.notEqual(filtered.status, 0);
  assert.match(filtered.stderr, /omit MANA_SYNC_MODE/);
  const env = { ...process.env }; delete env.MANA_SYNC_MODE;
  const failed = spawnSync(process.execPath, [runner, 'engine', '--shard=1/8', '--invalid-ci-test-option'],
    { env, encoding: 'utf8', windowsHide: true });
  assert.notEqual(failed.status, 0);
  assert.match(failed.stderr, /bad option|unknown option/);
});
