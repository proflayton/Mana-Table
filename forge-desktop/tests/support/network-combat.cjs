const assert = require('node:assert/strict');
const { zone, waitFor } = require('./top-library.cjs');
const { submitMatchAction } = require('./engine.cjs');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
// Partner commanders make the encounter deterministic without injecting game state.
const deck = 'Deck\n98 Forest\nCommander\n1 Anara, Wolvid Familiar\n1 Gilanra, Caller of Wirewood';
const commanderCosts = new Map([['Anara, Wolvid Familiar', 4], ['Gilanra, Caller of Wirewood', 3]]);
const card = (state, id) => state.players.flatMap(player => player.zones.flatMap(zone => zone.cards)).find(card => card.combatId === id || card.visualId === id);
async function submit(client, state, answer) {
  try {
    return await client.request('matchAction', { sessionId: state.id, promptId: state.prompt.id, ...answer });
  } catch (error) {
    const current = await client.request('matchState');
    error.message += ` [turn=${state.turn} phase=${state.phaseKey} viewer=${state.viewerId} input=${state.prompt?.inputType} prompt=${state.prompt?.id} action=${JSON.stringify(answer)} current=${current.prompt?.id}]`;
    throw error;
  }
}

async function startTable(clients) {
  const ids = [];
  for (let seat = 0; seat < clients.length; seat++) {
    await clients[seat].request('import', { name: `Combat seat ${seat + 1}`, format: 'Commander', text: deck });
    ids.push((await clients[seat].request('save')).id);
  }
  const lobby = await clients[0].request('multiplayerHost', { format: 'Commander', playerCount: clients.length, autoPortForward: false });
  assert.equal(lobby.error, null);
  const port = new URL(`http://${lobby.addresses[0].url.replace(/^https?:\/\//, '')}`).port;
  for (const guest of clients.slice(1)) await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
  for (const guest of clients.slice(1)) await waitFor(async () => (await guest.request('multiplayerState')).slots?.filter(slot => slot.type !== 'OPEN').length === clients.length, 'Guest did not join');
  for (let seat = 0; seat < clients.length; seat++) {
    await clients[seat].request('multiplayerSelectDeck', { deckId: ids[seat] });
    await clients[seat].request('multiplayerReady', { ready: true });
  }
  await waitFor(async () => (await clients[0].request('multiplayerState')).slots.every(slot => slot.ready && slot.deck), 'Players not ready');
  await clients[0].request('multiplayerStart');
}

async function playCombat(clients, attackingSeat, interact = async ({ client, state, answer }) => submit(client, state, answer)) {
  const previous = new Map(), blockSeats = new Set(), lastStates = [];
  let declared = false, completed = false, attackTurn, lastState;
  const deadline = Date.now() + 150000;
  for (; Date.now() < deadline && !completed;) {
    for (let seat = 0; seat < clients.length; seat++) {
      const client = clients[seat];
      let state = await client.request('matchState');
      if (!state?.players?.length) continue;
      assert.notEqual(state.status, 'error', state.error);
      assert.notEqual(state.status, 'finished', 'Combat encounter ended early');
      for (const other of state.players.filter(player => player.id !== state.viewerId)) assert.deepEqual(zone(state, 'Hand', other.id).cards, []);
      if (declared && state.turn === attackTurn && state.phaseKey === 'MAIN2') { completed = true; break; }
      const prompt = state.prompt;
      lastState = { seat, turn: state.turn, phase: state.phaseKey, prompt, combat: state.combat };
      lastStates[seat] = { ...lastState, previous: previous.get(client),
        field: zone(state, 'Battlefield').cards.map(card => ({ name: card.name, tapped: card.tapped, selectable: card.selectable })),
        hand: zone(state, 'Hand').cards.map(card => ({ name: card.name, selectable: card.selectable })) };
      if (!prompt || previous.get(client) === prompt.id) continue;
      if (['InputAttack', 'InputBlock'].includes(prompt.inputType)) assert.ok(state.combat, 'Combat input must include battlefield assignments and legal pairs');
      if (prompt.kind === 'input' && !prompt.okEnabled && !prompt.cancelEnabled && !prompt.playerChoices?.length) continue;
      const field = zone(state, 'Battlefield').cards, hand = zone(state, 'Hand').cards;
      let answer;
      if (prompt.inputType === 'InputAttack' && seat === attackingSeat && !declared && state.combat?.attackOptions.length >= 2
          && state.players.every(player => zone(state, 'Battlefield', player.id).cards.filter(card => card.type.includes('Creature') && !card.tapped).length >= 2)) {
        const opponents = state.players.filter(player => player.id !== state.viewerId);
        const options = state.combat.attackOptions.filter(option => opponents.every(player => option.defenders.some(defender => defender.kind === 'player' && defender.id === player.id))).slice(0, 2);
        if (options.length < 2) continue;
        assert.equal(prompt.canAutoPass, false);
        const ids = options.map(option => option.cardId);
        const fresh = () => waitFor(async () => {
          const current = await client.request('matchState');
          return current.prompt?.inputType === 'InputAttack' && current.prompt.okEnabled && current;
        }, 'Attack selection did not become ready');
        const assign = async (id, defender) => {
          state = await fresh();
          const answer = { action: 'attack', attackerKey: card(state, id).key, defenderPlayerId: defender };
          const old = state;
          await interact({ stage: 'attack', client, seat, state, answer, attackerId: id, defenderId: defender });
          await waitFor(async () => {
            const current = await client.request('matchState');
            return current.prompt?.id && current.prompt.id !== old.prompt.id;
          }, 'Attack prompt did not refresh');
          await assert.rejects(submit(client, old, answer), /choice has changed/);
          state = await fresh();
        };
        await assert.rejects(submit(client, state, { action: 'attack', attackerKey: card(state, ids[0]).key, defenderPlayerId: state.viewerId }), /cannot attack/);
        await assign(ids[0], opponents[0].id);
        assert.equal(state.combat.attackers.find(attack => attack.cardId === ids[0]).defender.id, opponents[0].id);
        await assign(ids[0], opponents[1].id); // Change defender, then recall and redeclare.
        assert.equal(state.combat.attackers.find(attack => attack.cardId === ids[0]).defender.id, opponents[1].id);
        await assign(ids[0], opponents[1].id);
        assert.equal(state.combat.attackers.length, 0);
        await assign(ids[0], opponents[0].id);
        await assign(ids[1], opponents[1].id);
        assert.deepEqual(new Set(state.combat.attackers.map(attack => attack.defender.id)), new Set(opponents.map(player => player.id)));
        for (const observer of clients) await waitFor(async () => (await observer.request('matchState')).combat?.attackers.length === 2, 'An observer cannot see declared attackers');
        await interact({ stage: 'confirmAttack', client, seat, state, answer: { action: 'ok' } });
        declared = true; attackTurn = state.turn; previous.set(client, state.prompt.id); continue;
      } else if (declared && prompt.inputType === 'InputBlock') {
        assert.equal(prompt.canAutoPass, false);
        const attack = state.combat.attackers.find(attack => attack.defendingPlayerId === state.viewerId && attack.eligibleBlockerIds.length);
        assert.ok(attack, JSON.stringify(lastState));
        const blocker = attack.eligibleBlockerIds[0];
        const wrong = state.combat.attackers.find(other => other.defendingPlayerId !== state.viewerId);
        assert.ok(wrong);
        assert.deepEqual(wrong.eligibleBlockerIds, [], 'Cannot block for another player');
        await assert.rejects(submit(client, state, { action: 'block', attackerKey: card(state, wrong.cardId).key, blockerKey: card(state, blocker).key }), /cannot block/);
        for (const expected of [true, false, true]) {
          const oldId = state.prompt.id;
          await interact({ stage: 'block', client, seat, state, attackerId: attack.cardId, blockerId: blocker,
            answer: { action: 'block', attackerKey: card(state, attack.cardId).key, blockerKey: card(state, blocker).key } });
          state = await waitFor(async () => {
            const current = await client.request('matchState');
            return current.prompt?.inputType === 'InputBlock' && current.prompt.id !== oldId
              && current.combat.attackers.find(other => other.cardId === attack.cardId)?.blockerIds.includes(blocker) === expected && current;
          }, 'Block assignment did not update');
        }
        for (const observer of clients) await waitFor(async () => (await observer.request('matchState')).combat?.attackers.find(other => other.cardId === attack.cardId)?.blockerIds.includes(blocker), 'Observer cannot see the block');
        await interact({ stage: 'confirmBlock', client, seat, state, answer: { action: 'ok' } });
        blockSeats.add(seat); previous.set(client, state.prompt.id); continue;
      } else if (prompt.inputType === 'InputPassPriority') {
        if (!prompt.okEnabled) continue;
        const ownMain = state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1' && !state.stack.length;
        const land = ownMain && hand.find(card => card.type.includes('Land') && card.selectable);
        // Forge can let a selectable card enter payment even when it is not
        // affordable. This all-Forest fixture must build enough mana first;
        // neither partner has a tax before this encounter's first combat.
        const mana = field.filter(card => card.name === 'Forest' && !card.tapped).length
          + (state.players.find(player => player.id === state.viewerId).mana?.G || 0);
        const creature = ownMain && zone(state, 'Command').cards.find(card => card.selectable && mana >= commanderCosts.get(card.name));
        answer = land || creature ? { action: 'card', key: (land || creature).key } : { action: 'ok' };
      } else if (prompt.kind === 'choice') answer = { choices: Array.from({ length: Math.max(prompt.min, Math.min(1, prompt.max)) }, (_, i) => i) };
      else if (prompt.kind === 'reveal') answer = { action: 'ack' };
      else if (prompt.okEnabled) answer = { action: 'ok' };
      else if (prompt.playerChoices?.length) answer = { action: 'player', playerId: prompt.playerChoices[0] };
      else if (prompt.inputType === 'InputSelectCardsFromList') {
        const discard = hand.find(card => card.selectable && !card.highlighted);
        if (discard) answer = { action: 'card', key: discard.key };
      }
      // Setup advances through asynchronous priority/payment displays. Reselect
      // from fresh state on a rejected stale setup prompt; combat edits above
      // are deliberately strict and must never need retrying.
      if (answer && await submitMatchAction(client, state, answer)) previous.set(client, prompt.id);
    }
    await sleep(25);
  }
  assert.ok(declared && completed && blockSeats.size === 2, JSON.stringify({ declared, completed, blockSeats: [...blockSeats], lastStates }));
}

module.exports = { startTable, playCombat, submit };
