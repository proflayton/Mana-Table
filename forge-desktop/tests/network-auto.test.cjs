const { test } = require('node:test');
const assert = require('node:assert/strict');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

for (const playerCount of [2, 3]) test(`network Auto preserves land plays and affordable commanders at a ${playerCount}-player table`, { timeout: 180000 }, async () => {
  const engines = Array.from({ length: playerCount }, (_, index) => startEngine(testProfile(index ? 'auto-guest' : 'auto-host')));
  const [host, ...guests] = engines;
  async function waitFor(check, message) {
    const until = Date.now() + 20000;
    while (Date.now() < until) {
      const result = await check();
      if (result) return result;
      await sleep(40);
    }
    throw new Error(message);
  }
  try {
    await Promise.all(engines.map(ready));
    const deckIds = [];
    for (const engine of engines) {
      await engine.request('import', { name: 'Network Auto regression', format: 'Commander', text: 'Deck\n99 Forest\nCommander\n1 Rhys the Redeemed' });
      const saved = await engine.request('save');
      deckIds.push(saved.id);
    }
    const lobby = await host.request('multiplayerHost', { format: 'Commander', playerCount });
    assert.equal(lobby.error, null);
    const port = new URL(`http://${lobby.addresses[0].url.replace(/^https?:\/\//, '')}`).port;
    assert.ok(port, JSON.stringify(lobby.addresses));
    for (const guest of guests) await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    for (const guest of guests) {
      await waitFor(async () => (await guest.request('multiplayerState')).slots?.filter(slot => slot.type !== 'OPEN').length === playerCount, 'Guest did not join');
    }
    for (let i = 0; i < engines.length; i++) {
      await engines[i].request('multiplayerSelectDeck', { deckId: deckIds[i] });
      await engines[i].request('multiplayerReady', { ready: true });
    }
    await waitFor(async () => (await host.request('multiplayerState')).slots.every(slot => slot.ready && slot.deck), 'Players did not become ready');
    await host.request('multiplayerStart');
    const progress = engines.map(() => ({ playedLand: false, verified: false, automaticPasses: 0, previous: null }));
    const until = Date.now() + 90000;
    while (Date.now() < until && !progress.every(seat => seat.verified)) {
      for (let i = 0; i < engines.length; i++) {
        const engine = engines[i], seat = progress[i];
        const state = await engine.request('matchState');
        if (!state) continue;
        assert.notEqual(state.status, 'error', state.error);
        const prompt = state.prompt;
        seat.observed = { turn: state.turn, phase: state.phaseKey, prompt };
        if (!prompt || prompt.id === seat.previous) continue;
        const scope = { sessionId: state.id, promptId: prompt.id };
        let answer;
        if (prompt.inputType === 'InputPassPriority') {
          // Input identity arrives before showMessageInitial/updateButtons.
          // Keep polling this same sequence until Continue is actually enabled;
          // canAutoPass=false is not permission to send an ordinary OK instead.
          if (!prompt.okEnabled) continue;
          if (!seat.verified && state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1' && !state.stack.length) {
            const player = state.players.find(player => player.id === state.viewerId);
            assert.equal(prompt.canAutoPass, false, `${i ? 'Guest' : 'Host'} must keep priority with ${seat.playedLand ? 'an affordable commander' : 'a land play'}`);
            await assert.rejects(engine.request('matchAction', { ...scope, action: 'passIfNoResponse' }), /needs your decision/);
            assert.equal((await engine.request('matchState')).prompt.id, prompt.id);
            if (!seat.playedLand) {
              answer = { action: 'card', key: player.zones.find(zone => zone.name === 'Hand').cards[0].key };
              seat.playedLand = true;
            } else {
              seat.verified = true;
              answer = { action: 'ok' };
            }
          } else answer = { action: prompt.canAutoPass ? 'passIfNoResponse' : 'ok' };
        } else if (prompt.kind === 'choice') {
          answer = { choices: Array.from({ length: prompt.min }, (_, index) => index) };
        } else if (prompt.kind === 'reveal') answer = { action: 'ack' };
        else if (prompt.playerChoices?.length) {
          // Three-player games begin with a required starting-player selection.
          answer = { action: 'player', playerId: prompt.playerChoices.includes(state.viewerId) ? state.viewerId : prompt.playerChoices[0] };
        }
        else if (prompt.okEnabled) answer = { action: 'ok' };
        else continue;
        try {
          await engine.request('matchAction', { ...scope, ...answer });
        } catch (error) {
          throw new Error(`Seat ${i}, ${JSON.stringify({ turn: state.turn, phase: state.phaseKey, prompt, answer })}: ${error.message}`, { cause: error });
        }
        seat.previous = prompt.id;
        if (answer.action === 'passIfNoResponse') seat.automaticPasses++;
      }
      await sleep(30);
    }
    assert.ok(progress.every(seat => seat.verified), JSON.stringify(progress));
    assert.ok(progress.every(seat => seat.automaticPasses > 0), 'Every seat must actually advance automatically when no play is available');
  } finally {
    await Promise.all(engines.map(engine => engine.close()));
  }
});
