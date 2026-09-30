const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');
const { networkDeck, playChorus, waitFor } = require('./support/top-library.cjs');

for (const cardSeat of [0, 1]) test(`network Elven Chorus for the ${cardSeat === 0 ? 'host' : 'guest'} exposes only the authorized top card, permits creature casts, and revokes visibility`, { timeout: 180000 }, async () => {
  const clients = [startEngine(testProfile('top-library-host')), startEngine(testProfile('top-library-guest'))];
  const [host, guest] = clients;
  try {
    await Promise.all(clients.map(ready));
    const ids = [];
    for (let i = 0; i < clients.length; i++) {
      await clients[i].request('import', { name: 'Library visibility', format: 'Constructed', text: i === cardSeat ? networkDeck : 'Deck\n60 Forest' });
      ids.push((await clients[i].request('save')).id);
    }
    const lobby = await host.request('multiplayerHost', { format: 'Constructed', playerCount: 2, autoPortForward: false });
    assert.equal(lobby.error, null);
    const port = new URL(`http://${lobby.addresses[0].url.replace(/^https?:\/\//, '')}`).port;
    await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    await waitFor(async () => (await guest.request('multiplayerState')).slots?.filter(slot => slot.type !== 'OPEN').length === 2, 'Guest did not join');
    for (let i = 0; i < clients.length; i++) {
      await clients[i].request('multiplayerSelectDeck', { deckId: ids[i] });
      await clients[i].request('multiplayerReady', { ready: true });
    }
    await waitFor(async () => (await host.request('multiplayerState')).slots.every(slot => slot.ready && slot.deck), 'Players not ready');
    await host.request('multiplayerStart');
    await playChorus(cardSeat === 0 ? clients : [guest, host], { requireCreature: true });
  } finally { await Promise.all(clients.map(client => client.close())); }
});
