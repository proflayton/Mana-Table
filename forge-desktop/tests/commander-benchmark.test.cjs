const { test } = require('node:test');
const assert = require('node:assert/strict');
const { isDeepStrictEqual } = require('node:util');
const fs = require('node:fs');
const path = require('node:path');
const { startEngine, testProfile, ready } = require('./support/engine.cjs');
const { startTable, playCombat, submit } = require('./support/network-combat.cjs');
const { waitFor, zone } = require('./support/top-library.cjs');

// Same-match observer comparison only. These Forge IDs are not portable engine
// identities, and this projection does not claim to cover hidden rules state.
function publicTable(state) {
  const publicZones = new Set(['Battlefield', 'Command', 'Graveyard', 'Exile']);
  return {
    turn: state.turn, phase: state.phaseKey, activePlayerId: state.activePlayerId,
    players: state.players.map(player => ({
      id: player.id, life: player.life, eliminated: player.eliminated,
      commanderDamage: player.commanderDamage.map(({ name, ownerId, damage }) => ({ name, ownerId, damage })),
      mana: player.mana, priority: player.priority,
      zones: player.zones.map(zone => ({
        name: zone.name, count: zone.count,
        cards: publicZones.has(zone.name) ? zone.cards.map(card => ({
          id: card.visualId, name: card.name, type: card.type,
          power: card.power, toughness: card.toughness, tapped: card.tapped,
          sick: card.sick, damage: card.damage, counters: card.counters,
          attacking: card.attacking, blocking: card.blocking, faceDown: card.faceDown
        })).sort((a, b) => a.id.localeCompare(b.id)) : []
      }))
    })),
    stack: state.stack.map(item => ({ id: item.id, name: item.name, ability: item.ability })),
    combat: state.combat ? {
      attackingPlayerId: state.combat.attackingPlayerId,
      attackers: state.combat.attackers.map(attack => ({
        cardId: attack.cardId, defendingPlayerId: attack.defendingPlayerId,
        blockerIds: [...attack.blockerIds].sort(), blocked: attack.blocked
      })).sort((a, b) => a.cardId.localeCompare(b.cardId))
    } : null
  };
}

for (const transport of (process.env.MANA_SYNC_MODE ? [process.env.MANA_SYNC_MODE] : ['local', 'network', 'mixed']))
test(`four-player Commander benchmark (${transport}): guest attacks, two defenders block, all four clients agree`, { timeout: 300000 }, async () => {
  const profile = testProfile('commander-benchmark');
  const clients = [];
  const evidence = { schemaVersion: 2, scenario: 'commander-four-player-combat-v1', transport, actions: [], checkpoints: [] };
  let latest;
  async function checkpoint(name) {
    try {
      await waitFor(async () => {
        latest = await Promise.all(clients.map(client => client.request('matchState')));
        for (const state of latest) {
          assert.notEqual(state?.status, 'error', state?.error);
          if (state?.players?.length !== 4) return false;
          for (const player of state.players) {
            assert.deepEqual(zone(state, 'Library', player.id).cards, [], 'Libraries must remain private');
            if (player.id !== state.viewerId) assert.deepEqual(zone(state, 'Hand', player.id).cards, [], 'Opponent hands must remain private');
          }
        }
        const tables = latest.map(publicTable);
        if (!tables.every(table => isDeepStrictEqual(table, tables[0]))) return false;
        const authoritative = await clients[0].request('testMatchViews');
        return latest.every(state => isDeepStrictEqual(state, authoritative.find(view => view.viewerId === state.viewerId)));
      }, `Clients did not agree at ${name}`);
    } catch (error) {
      if (latest?.every(state => state?.players?.length === 4)) {
        for (const state of latest.slice(1)) assert.deepEqual(publicTable(state), publicTable(latest[0]), `${name}: public state differs`);
      }
      throw error;
    }
    assert.equal(new Set(latest.map(state => state.viewerId)).size, 4, 'Four distinct human seats must be connected');
    if (name === 'commanders-ready') for (const player of latest[0].players) {
      assert.equal(player.life, 40);
      assert.equal(player.commanderDamage.length, 8, 'All eight partner commanders must have damage totals');
      assert.equal(zone(latest[0], 'Command', player.id).count, 0);
      assert.deepEqual(zone(latest[0], 'Battlefield', player.id).cards.filter(card => card.type.includes('Creature')).map(card => card.name).sort(),
        ['Anara, Wolvid Familiar', 'Gilanra, Caller of Wirewood']);
    }
    if (name === 'attackers-assigned') {
      const { attackers, attackingPlayerId } = latest[0].combat;
      assert.equal(attackers.length, 2);
      assert.equal(new Set(attackers.map(attack => attack.defendingPlayerId)).size, 2);
      assert.equal(latest[0].players.filter(player => player.id !== attackingPlayerId && !attackers.some(attack => attack.defendingPlayerId === player.id)).length, 1,
        'The fourth player observes combat without being attacked');
    }
    evidence.checkpoints.push({ name, publicState: publicTable(latest[0]), exactSeatViewsVerified: latest.map(state => ({ viewerId: state.viewerId, revision: state.revision })) });
    assert.ok(latest.every(state => state.activity.length > 0), 'Every seat receives the same activity projection path');
    if (name === 'commanders-ready' && transport !== 'local') {
      const before = await clients[2].request('matchState');
      await clients[2].request('multiplayerReconnect');
      const after = await clients[2].request('matchState');
      assert.deepEqual(after, before, 'Reconnect restores exactly this seat, private view and pending decision');
      evidence.reconnectedSeat = 2;
    }
  }
  try {
    if (transport === 'local') {
      const host = startEngine(path.join(profile, 'host')); clients.push(host); await ready(host);
      for (let seat = 1; seat < 4; seat++) {
        let connectionId;
        clients.push({
          async request(method, args = {}) {
            if (method === 'import' || method === 'save') return host.request(method, args);
            if (method === 'multiplayerJoin') { ({ connectionId } = await host.request('multiplayerLocalJoin')); return; }
            return host.request(method, { ...args, connectionId });
          },
          async close() { if (connectionId) await host.request('multiplayerClose', { connectionId }); }
        });
      }
    } else {
      for (let seat = 0; seat < 4; seat++) clients.push(startEngine(path.join(profile, `seat-${seat}`)));
      await Promise.all(clients.map(ready));
    }
    await startTable(clients, transport);
    await playCombat(clients, 1, async interaction => {
      const { client, seat, state, answer, stage } = interaction;
      evidence.actions.push({ seat, stage, turn: state.turn, phase: state.phaseKey, promptId: state.prompt.id, answer });
      return submit(client, state, answer);
    }, checkpoint);
    assert.equal(evidence.checkpoints.length, 5);
    assert.equal(evidence.checkpoints.at(-1).publicState.phase, 'MAIN2');
  } catch (error) {
    evidence.failure = { message: error.message, snapshots: latest };
    throw error;
  } finally {
    try {
      fs.writeFileSync(path.join(profile, 'benchmark.json'), JSON.stringify(evidence, null, 2) + '\n');
      console.log(`Four-player benchmark evidence: ${path.join(profile, 'benchmark.json')}`);
    } finally { for (const client of [...clients].reverse()) await client.close(); }
  }
});
