const assert = require('node:assert/strict');
const { submitMatchAction } = require('./engine.cjs');
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

async function startChorusTable(clients, cardSeat, { deck = networkDeck } = {}) {
  const [host, guest] = clients, ids = [];
  for (let i = 0; i < clients.length; i++) {
    await clients[i].request('import', { name: 'Library visibility', format: 'Constructed', text: i === cardSeat ? deck : 'Deck\n60 Forest' });
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
}

function createLibraryDrawObserver({ now = Date.now } = {}) {
  const started = now(), setupDeadline = started + 100000;
  const observed = new Map(), transitions = [], actions = [];
  let firstTopAt, drawn, previous;
  return {
    observe(state) {
      const library = zone(state, 'Library'), hand = zone(state, 'Hand').cards;
      for (const card of hand) {
        const top = observed.get(card.visualId);
        if (!drawn && top && !top.alreadyInHand && library.count < top.libraryCount)
          drawn = { visualId: card.visualId, name: card.name, observedTurn: top.turn, drawnTurn: state.turn,
            observedLibraryCount: top.libraryCount, drawnLibraryCount: library.count, elapsedMs: now() - started };
      }
      if (library.topCard?.visualId && !observed.has(library.topCard.visualId)) {
        firstTopAt ??= now();
        observed.set(library.topCard.visualId, { name: library.topCard.name, turn: state.turn, libraryCount: library.count,
          alreadyInHand: hand.some(card => card.visualId === library.topCard.visualId), elapsedMs: now() - started });
      }
      const transition = { turn: state.turn, phase: state.phaseKey, prompt: state.prompt?.inputType ?? state.prompt?.kind,
        libraryCount: library.count, top: library.topCard?.visualId ?? null,
        hand: hand.map(card => ({ id: card.visualId, name: card.name })) };
      const key = JSON.stringify(transition);
      if (key !== previous) {
        previous = key; transitions.push({ elapsedMs: now() - started, ...transition });
        if (transitions.length > 64) transitions.shift();
      }
    },
    action(seat, state, answer, applied) {
      actions.push({ elapsedMs: now() - started, seat, turn: state.turn, phase: state.phaseKey,
        prompt: state.prompt?.inputType ?? state.prompt?.kind, promptId: state.prompt?.id, answer, applied });
      if (actions.length > 32) actions.shift();
    },
    get drewTop() { return Boolean(drawn); },
    // Late random setup must still leave time for the next draw and removal.
    // Ordinary polls/actions never extend either bounded milestone budget.
    get deadline() { return firstTopAt == null ? setupDeadline : Math.max(setupDeadline, firstTopAt + 30000); },
    snapshot() { return { elapsedMs: now() - started, firstTopMs: firstTopAt == null ? null : firstTopAt - started,
      draw: drawn ?? null, observedTop: [...observed].map(([visualId, card]) => ({ visualId, ...card })), transitions, actions }; }
  };
}

// Drive actual engine decisions. No fixture injects hidden cards or permissions.
async function playChorus(clients, { requireCreature = false, inspect = async () => {}, report = () => {} } = {}) {
  let sawTop = false, sawLand = false, castCreature = false, removed = false;
  let removing = false, lastState, castingCreatureId;
  const waiting = new Map();
  const answered = new Map();
  const observer = createLibraryDrawObserver();
  while (Date.now() < observer.deadline && !removed) {
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
        observer.observe(state);
        lastState = { turn: state.turn, phase: state.phaseKey, prompt: state.prompt, hand: hand.map(card => card.name) };
        if (castingCreatureId && field.some(card => card.visualId === castingCreatureId)) castCreature = true;
        if (chorus && library.topCard) {
          sawTop = true;
          assert.ok(library.cards.some(card => card.key === library.topCard.key && card.visualId === library.topCard.visualId));
          if (library.topCard.type.includes('Land')) {
            sawLand = true;
            assert.equal(library.topCard.selectable, false, 'Chorus lets you look at a land, not play it');
          }
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
      let answer, progress;
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
          progress = () => { castingCreatureId = library.topCard.visualId; };
        } else if (chorus && sawLand && observer.drewTop && (!requireCreature || castCreature) && removal && mana >= 2) {
          answer = { action: 'card', key: removal.key }; progress = () => { removing = true; };
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
        const applied = await submitMatchAction(client, state, answer);
        observer.action(index, state, answer, applied);
        if (applied) { answered.set(client, prompt.id); progress?.(); }
      } catch (error) {
        error.message += ` (${JSON.stringify({ prompt, answer })})`;
        throw error;
      }
      await sleep(15);
    }
    await sleep(15);
  }
  const evidence = { sawTop, sawLand, drewTop: observer.drewTop, castCreature, removed, lastState, ...observer.snapshot() };
  report(evidence);
  assert.ok(sawTop && sawLand && observer.drewTop && removed && (!requireCreature || castCreature), JSON.stringify(evidence));
  return evidence;
}

module.exports = { zone, localDeck, networkDeck, startChorusTable, playChorus, waitFor, createLibraryDrawObserver };
