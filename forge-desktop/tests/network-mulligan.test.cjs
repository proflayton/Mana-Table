const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

for (const playerCount of [2, 3]) test(`network mulligans let players select their bottom cards at a ${playerCount}-player table`, { timeout: 180000 }, async () => {
  const engines = Array.from({ length: playerCount }, () => startEngine(testProfile('network-mulligan')));
  const [host, ...guests] = engines;
  const progress = engines.map((_, index) => ({ mulligans: 0, target: index === 1 ? 2 : index === 0 ? 1 : 0, kept: false, tucked: false }));
  const hand = state => state.players.find(player => player.id === state.viewerId).zones.find(zone => zone.name === 'Hand').cards;
  async function waitFor(check, message) {
    const deadline = Date.now() + 20000;
    while (Date.now() < deadline) {
      const result = await check();
      if (result) return result;
      await sleep(50);
    }
    throw new Error(`${message}: ${JSON.stringify(progress)}`);
  }
  try {
    await Promise.all(engines.map(ready));
    const deckIds = [];
    for (const engine of engines) {
      await engine.request('import', { name: 'Mulligan regression', format: 'Commander', text: 'Deck\n99 Forest\nCommander\n1 Rhys the Redeemed' });
      deckIds.push((await engine.request('save')).id);
    }
    const lobby = await host.request('multiplayerHost', { format: 'Commander', playerCount, autoPortForward: false });
    assert.equal(lobby.error, null);
    const port = new URL(`http://${lobby.addresses[0].url.replace(/^https?:\/\//, '')}`).port;
    for (const guest of guests) await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    for (const guest of guests) await waitFor(async () => (await guest.request('multiplayerState')).slots?.filter(slot => slot.type !== 'OPEN').length === playerCount, 'Guest did not join');
    for (let i = 0; i < engines.length; i++) {
      await engines[i].request('multiplayerSelectDeck', { deckId: deckIds[i] });
      await engines[i].request('multiplayerReady', { ready: true });
    }
    await waitFor(async () => (await host.request('multiplayerState')).slots.every(slot => slot.ready && slot.deck), 'Players did not become ready');
    await host.request('multiplayerStart');
    const deadline = Date.now() + 45000;
    let started = false;
    while (Date.now() < deadline && !started) {
      for (let i = 0; i < engines.length; i++) {
        const engine = engines[i], seat = progress[i];
        const state = await engine.request('matchState');
        if (!state) continue;
        assert.notEqual(state.status, 'error', state.error);
        const prompt = state.prompt;
        seat.observed = { turn: state.turn, phase: state.phaseKey, prompt };
        for (const player of state.players.filter(player => player.id !== state.viewerId)) {
          assert.deepEqual(player.zones.find(zone => zone.name === 'Hand').cards, [], 'Other opening hands must stay private');
        }
        if (state.turn > 0) { started = true; break; }
        if (!prompt) continue;
        assert.equal(prompt.canAutoPass ?? false, false, 'Auto must never answer an opening-hand decision');
        let answer;
        if (prompt.inputType === 'InputConfirmMulligan' && prompt.okEnabled && prompt.cancelEnabled) {
          assert.equal(hand(state).length, 7 - Math.max(0, seat.mulligans - (playerCount > 2 ? 1 : 0)));
          if (seat.mulligans < seat.target) { answer = { action: 'cancel' }; seat.mulligans++; }
          else { answer = { action: 'ok' }; seat.kept = true; }
        } else if (prompt.inputType === 'InputLondonMulligan' && prompt.cancelEnabled) {
          const cards = hand(state);
          assert.equal(cards.length, 7);
          assert.ok(cards.every(card => card.selectable), `Seat ${i} cannot select cards to put on the bottom: ${JSON.stringify(prompt)}`);
          const card = cards[0];
          const scope = { sessionId: state.id, promptId: prompt.id };
          await engine.request('matchAction', { ...scope, action: 'card', key: card.key });
          await waitFor(async () => hand(await engine.request('matchState')).some(item => item.visualId === card.visualId && item.highlighted), 'Bottom-card selection was not highlighted');
          // Selecting again must undo the choice; the player can change their mind.
          await engine.request('matchAction', { ...scope, action: 'card', key: card.key });
          await waitFor(async () => !hand(await engine.request('matchState')).some(item => item.highlighted), 'Bottom-card selection did not toggle off');
          await engine.request('matchAction', { ...scope, action: 'card', key: card.key });
          const toReturn = seat.mulligans - (playerCount > 2 ? 1 : 0);
          for (const extra of cards.slice(1, toReturn)) {
            await engine.request('matchAction', { ...scope, action: 'card', key: extra.key });
          }
          await waitFor(async () => (await engine.request('matchState')).prompt?.okEnabled, 'Keep was not enabled after choosing a bottom card');
          seat.tucked = true;
          answer = { action: 'ok' };
        } else if (prompt.kind === 'choice') answer = { choices: Array.from({ length: prompt.min }, (_, index) => index) };
        else if (prompt.playerChoices?.length) answer = { action: 'player', playerId: prompt.playerChoices[0] };
        else if (prompt.okEnabled) answer = { action: 'ok' };
        if (!answer) continue;
        await engine.request('matchAction', { sessionId: state.id, promptId: prompt.id, ...answer });
        await waitFor(async () => (await engine.request('matchState')).prompt?.id !== prompt.id, 'Opening-hand input did not finish');
      }
      await sleep(30);
    }
    assert.ok(started, `Game did not start: ${JSON.stringify(progress)}`);
    assert.ok(progress.every(seat => seat.kept), JSON.stringify(progress));
    assert.ok(progress[1].tucked, 'Remote player must choose a bottom card after their second mulligan');
    for (const seat of progress) assert.equal(seat.mulligans, seat.target);
  } finally {
    await Promise.all(engines.map(engine => engine.close()));
  }
});
