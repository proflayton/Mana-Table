const { test } = require('node:test');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');
const { startChorusTable, playChorus } = require('./support/top-library.cjs');

for (const cardSeat of [0, 1]) test(`network Elven Chorus for the ${cardSeat === 0 ? 'host' : 'guest'} exposes only the authorized top card, permits creature casts, and revokes visibility`, { timeout: 180000 }, async () => {
  const clients = [startEngine(testProfile('top-library-host')), startEngine(testProfile('top-library-guest'))];
  const [host, guest] = clients;
  try {
    await Promise.all(clients.map(ready));
    await startChorusTable(clients, cardSeat);
    await playChorus(cardSeat === 0 ? clients : [guest, host], { requireCreature: true });
  } finally { await Promise.all(clients.map(client => client.close())); }
});
