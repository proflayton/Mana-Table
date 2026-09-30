const assert = require('node:assert/strict');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const zone = (state, name, playerId = state.viewerId) => state.players.find(player => player.id === playerId).zones.find(item => item.name === name);
const creatures = ['Llanowar Elves', 'Elvish Mystic', 'Fyndhorn Elves', 'Arbor Elf', 'Young Wolf', 'Glistener Elf'];
const networkDeck = 'Deck\n28 Forest\n4 Elven Chorus\n4 Naturalize\n' + creatures.map(name => `4 ${name}`).join('\n');
const localDeck = 'Deck\n52 Forest\n4 Elven Chorus\n4 Naturalize';

async function waitFor(check, message, timeout = 20000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const result = await check();
    if (result) return result;
    await sleep(30);
  }
  throw new Error(message);
}

// Drive actual engine decisions. No fixture injects hidden cards or permissions.
async function playChorus(clients, { requireCreature = false, inspect = async () => {} } = {}) {
  let sawTop = false, sawLand = false, castCreature = false, removed = false, previousTop, drewTop = false;
  let removing = false, lastState, castingCreatureId;
  const waiting = new Map();
  const answered = new Map();
  const deadline = Date.now() + 100000;
  while (Date.now() < deadline && !removed) {
    for (let index = 0; index < clients.length; index++) {
      const client = clients[index], state = await client.request('matchState');
      if (!state?.players?.length) continue;
      assert.notEqual(state.status, 'error', state.error);
      assert.notEqual(state.status, 'finished', 'Encounter ended before checking Elven Chorus');
      const library = zone(state, 'Library'), field = zone(state, 'Battlefield').cards, hand = zone(state, 'Hand').cards;
      const chorus = field.find(card => card.name === 'Elven Chorus');
      for (const opponent of state.players.filter(player => player.id !== state.viewerId)) {
        assert.equal(zone(state, 'Library', opponent.id).topCard, null, 'Private look permission must not reveal the top card to opponents');
        assert.deepEqual(zone(state, 'Library', opponent.id).cards, []);
      }
      if (index === 0) {
        lastState = { turn: state.turn, phase: state.phaseKey, prompt: state.prompt, hand: hand.map(card => card.name) };
        if (castingCreatureId && field.some(card => card.visualId === castingCreatureId)) castCreature = true;
        if (chorus && library.topCard) {
          sawTop = true;
          assert.ok(library.cards.some(card => card.key === library.topCard.key && card.visualId === library.topCard.visualId));
          if (library.topCard.type.includes('Land')) {
            sawLand = true;
            assert.equal(library.topCard.selectable, false, 'Chorus lets you look at a land, not play it');
          }
          if (previousTop && hand.some(card => card.visualId === previousTop)) drewTop = true;
          previousTop = library.topCard.visualId;
          await inspect(state);
        }
        if (removing && !chorus && !state.stack.length && field.length) {
          assert.equal(library.topCard, null, 'Removing Chorus must hide the top card again');
          assert.deepEqual(library.cards, []);
          removed = true;
          break;
        }
      }
      const prompt = state.prompt;
      if (!prompt || answered.get(client) === prompt.id) continue;
      if (prompt.inputType === 'InputPassPriority' && !prompt.okEnabled) continue;
      let answer;
      if (index === 0 && prompt.inputType === 'InputPassPriority' && state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1' && !state.stack.length) {
        const land = hand.find(card => card.type.includes('Land') && card.selectable);
        const enchantment = hand.find(card => card.name === 'Elven Chorus');
        const removal = hand.find(card => card.name === 'Naturalize');
        const mana = field.filter(card => card.name === 'Forest' && !card.tapped).length;
        if (land) answer = { action: 'card', key: land.key };
        else if (!chorus && !removing && enchantment && mana >= 4) answer = { action: 'card', key: enchantment.key };
        else if (requireCreature && !castCreature && library.topCard?.type.includes('Creature') && library.topCard.selectable && mana >= 1) {
          assert.equal(prompt.canAutoPass, false, 'A playable creature on top must hold Auto');
          answer = { action: 'card', key: library.topCard.key };
          castingCreatureId = library.topCard.visualId;
        } else if (chorus && sawLand && drewTop && (!requireCreature || castCreature) && removal && mana >= 2) {
          answer = { action: 'card', key: removal.key }; removing = true;
        } else answer = { action: 'ok' };
      } else if (index === 0 && removing && prompt.inputType === 'InputSelectTargets') {
        if (chorus?.selectable) answer = { action: 'card', key: chorus.key };
        else if (prompt.okEnabled) answer = { action: 'ok' };
      } else if (prompt.kind === 'choice') answer = { choices: Array.from({ length: Math.max(prompt.min, Math.min(1, prompt.max)) }, (_, i) => i) };
      else if (prompt.kind === 'reveal') answer = { action: 'ack' };
      else if (prompt.okEnabled) answer = { action: 'ok' };
      else if (prompt.playerChoices?.length) answer = { action: 'player', playerId: prompt.playerChoices[0] };
      else if (prompt.kind === 'input') {
        const discard = hand.find(card => card.selectable && !card.highlighted && card.name === 'Forest')
          || hand.find(card => card.selectable && !card.highlighted);
        if (discard) answer = { action: 'card', key: discard.key };
      }
      if (!answer) {
        // Network input identity arrives before its message/buttons/selectables.
        const pending = waiting.get(client);
        if (pending?.id !== prompt.id) waiting.set(client, { id: prompt.id, since: Date.now() });
        else assert.ok(Date.now() - pending.since < 5000, `Unusable encounter decision: ${JSON.stringify(prompt)}`);
        continue;
      }
      waiting.delete(client);
      try {
        await client.request('matchAction', { sessionId: state.id, promptId: prompt.id, ...answer });
        answered.set(client, prompt.id);
      } catch (error) {
        if (error.message !== 'That choice has changed. Use the current prompt.') {
          error.message += ` (${JSON.stringify({ prompt, answer })})`;
          throw error;
        }
      }
      await sleep(15);
    }
    await sleep(15);
  }
  assert.ok(sawTop && sawLand && drewTop && removed && (!requireCreature || castCreature), JSON.stringify({ sawTop, sawLand, drewTop, castCreature, removed, lastState }));
}

module.exports = { zone, localDeck, networkDeck, playChorus, waitFor };
