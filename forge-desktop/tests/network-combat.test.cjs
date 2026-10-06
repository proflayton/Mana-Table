const { test } = require('node:test');
const { startEngine, testProfile, ready } = require('./support/engine.cjs');
const { startTable, playCombat } = require('./support/network-combat.cjs');

for (const seat of [0, 1]) test(`network combat: ${seat ? 'guest' : 'host'} assigns split attacks, recalls, and both defenders block`, { timeout: 240000 }, async () => {
  const clients = Array.from({ length: 3 }, () => startEngine(testProfile('network-combat')));
  try {
    await Promise.all(clients.map(ready));
    await startTable(clients);
    await playCombat(clients, seat);
  } finally { await Promise.all(clients.map(client => client.close())); }
});
