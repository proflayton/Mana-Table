const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');

test('a mistyped invite preserves the existing seat and its resumable connection', { timeout: 90000 }, async () => {
  const clients = [startEngine(testProfile('sync-lobby-host')), startEngine(testProfile('sync-lobby-guest'))];
  const [host, guest] = clients;
  try {
    await Promise.all(clients.map(ready));
    const lobby = await host.request('multiplayerHost', { format: 'Commander', playerCount: 2, autoPortForward: false });
    assert.match(lobby.startProblem, /1\/2/);
    const port = new URL(`http://${lobby.addresses[0].url}`).port;
    await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    await assert.rejects(guest.request('multiplayerJoin', { address: 'MT1-BROKEN' }), /incomplete or mistyped/);
    await assert.rejects(guest.request('multiplayerJoin', { address: 'not-an-endpoint' }), /host:port/);
    assert.equal((await guest.request('multiplayerState')).mode, 'joined');
    assert.equal((await host.request('multiplayerState')).slots.filter(slot => slot.type !== 'OPEN').length, 2);
    assert.equal((await guest.request('multiplayerReconnect')).mode, 'joined');
    const idle = await guest.request('multiplayerClose');
    assert.equal(idle.hosting, false);
    assert.equal((await host.request('multiplayerState')).slots[1].type, 'OPEN');
    await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    assert.equal((await guest.request('multiplayerState')).mode, 'joined');
  } finally { await Promise.all(clients.map(client => client.close())); }
});
