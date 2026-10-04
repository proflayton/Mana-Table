const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');
const { zone, waitFor } = require('./support/top-library.cjs');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const greenSpells = ['Llanowar Elves', 'Elvish Mystic', 'Fyndhorn Elves'];
const deck = 'Deck\n40 Forest\n4 Yavimaya Coast\n4 Sol Ring\n' + greenSpells.map(name => `4 ${name}`).join('\n');

async function responsive(promise, description) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${description} blocked the engine request loop`)), 5000);
    })]);
  } finally { clearTimeout(timer); }
}

for (const cardSeat of [0, 1]) test(`network ${cardSeat ? 'guest' : 'host'} can cancel and select Yavimaya Coast mana abilities during payment`, { timeout: 180000 }, async () => {
  const clients = [startEngine(testProfile('mana-choice-host')), startEngine(testProfile('mana-choice-guest'))];
  const [host, guest] = clients;
  let attempt, cancelled = false, cancellationVerified = false, colored = false, colorless = false, lastState;
  const previous = new Map();
  try {
    await Promise.all(clients.map(ready));
    const ids = [];
    for (let i = 0; i < clients.length; i++) {
      await clients[i].request('import', { name: 'Manual mana choices', format: 'Constructed', text: i === cardSeat ? deck : 'Deck\n60 Forest' });
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
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline && !(colored && colorless)) {
      for (let seat = 0; seat < clients.length; seat++) {
        const client = clients[seat], state = await responsive(client.request('matchState'), 'Polling during a mana choice');
        if (!state?.players?.length) continue;
        assert.notEqual(state.status, 'error', state.error);
        assert.notEqual(state.status, 'finished', 'Match ended before completing mana payments');
        const player = state.players.find(player => player.id === state.viewerId);
        const hand = zone(state, 'Hand').cards, field = zone(state, 'Battlefield').cards;
        const coast = field.find(card => card.name === 'Yavimaya Coast');
        const prompt = state.prompt;
        if (seat === cardSeat) {
          lastState = { turn: state.turn, phase: state.phaseKey, prompt, attempt, hand: hand.map(card => card.name) };
          if (attempt && field.some(card => card.visualId === attempt.spellId)) {
            assert.ok(attempt.paid, 'Spell must resolve using the manually chosen ability');
            assert.equal(player.life, attempt.life - (attempt.colored ? 1 : 0), 'Only colored mana deals Coast damage');
            if (attempt.colored) colored = true; else colorless = true;
            attempt = null;
          }
        }
        if (!prompt) continue;
        const decision = JSON.stringify([prompt.id, prompt.message, prompt.okEnabled, hand.map(card => card.highlighted)]);
        if (previous.get(client) === decision) continue;
        if (prompt.inputType === 'InputPassPriority' && !prompt.okEnabled) continue;
        let answer;
        if (seat === cardSeat && prompt.inputType === 'InputPassPriority' && state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1' && !state.stack.length) {
          const land = hand.find(card => card.name === 'Yavimaya Coast' && card.selectable);
          const spell = hand.find(card => !colored ? greenSpells.includes(card.name) : card.name === 'Sol Ring');
          if (!coast && land) answer = { action: 'card', key: land.key };
          else if (!attempt && coast && !coast.tapped && spell && !(colored && colorless)) {
            attempt = { colored: !colored, spellId: spell.visualId, life: player.life, paid: false };
            answer = { action: 'card', key: spell.key };
          } else answer = { action: 'ok' };
        } else if (seat === cardSeat && attempt && prompt.inputType?.startsWith('InputPayMana')) {
          if (attempt.paid) continue; // Payment can still be updating after the chosen ability starts.
          assert.ok(coast && !coast.tapped, 'Coast stays untapped until its ability is selected');
          if (cancelled && !cancellationVerified) {
            assert.equal(player.life, attempt.life);
            cancellationVerified = true;
          }
          answer = { action: 'card', key: coast.key };
          attempt.paymentPrompt = prompt.id;
        } else if (seat === cardSeat && attempt && prompt.kind === 'choice' && prompt.context === 'playAbility' && prompt.sourceCard?.name === 'Yavimaya Coast') {
          // The modal must remain readable across polls; the original payment action
          // cannot be replayed while Forge waits for this answer.
          assert.equal((await responsive(client.request('matchState'), 'Reading the ability chooser')).prompt.id, prompt.id);
          await assert.rejects(client.request('matchAction', { sessionId: state.id, promptId: attempt.paymentPrompt, action: 'card', key: coast.key }), /choice has changed/);
          if (!cancelled) {
            assert.equal(prompt.min, 0);
            cancelled = true;
            answer = { choices: [] };
          } else {
            const choice = prompt.choices.find(choice => attempt.colored ? /\{G\}|\{U\}/.test(choice.label) : /\{C\}/.test(choice.label));
            assert.ok(choice, JSON.stringify(prompt));
            answer = { choices: [choice.index] };
            attempt.paid = true;
          }
        } else if (seat === cardSeat && attempt && prompt.kind === 'choice' && /color/i.test(prompt.message)) {
          const green = prompt.choices.find(choice => /green/i.test(choice.label));
          assert.ok(green, JSON.stringify(prompt));
          answer = { choices: [green.index] };
          attempt.paid = true;
        } else if (prompt.kind === 'choice') answer = { choices: Array.from({ length: Math.max(prompt.min, Math.min(1, prompt.max)) }, (_, i) => i) };
        else if (prompt.kind === 'reveal') answer = { action: 'ack' };
        else if (prompt.okEnabled) answer = { action: 'ok' };
        else if (prompt.playerChoices?.length) answer = { action: 'player', playerId: prompt.playerChoices[0] };
        else if (prompt.inputType === 'InputSelectCardsFromList') {
          const discard = hand.find(card => card.name === 'Forest' && card.selectable && !card.highlighted)
            || hand.find(card => card.selectable && !card.highlighted && card.name !== 'Yavimaya Coast' && card.name !== 'Sol Ring');
          if (discard) answer = { action: 'card', key: discard.key };
        }
        if (!answer) continue;
        await responsive(client.request('matchAction', { sessionId: state.id, promptId: prompt.id, ...answer }), 'Selecting a mana ability');
        previous.set(client, decision);
      }
      await sleep(20);
    }
    assert.ok(cancelled && cancellationVerified && colored && colorless, JSON.stringify({ cancelled, cancellationVerified, colored, colorless, lastState }));
  } finally { await Promise.all(clients.map(client => client.close())); }
});
