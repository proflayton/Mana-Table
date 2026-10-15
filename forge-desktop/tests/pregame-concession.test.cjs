const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { startEngine, testProfile, ready } = require('./support/engine.cjs');
const { waitFor } = require('./support/top-library.cjs');

const transports = process.env.MANA_SYNC_MODE ? [process.env.MANA_SYNC_MODE] : ['local', 'network', 'mixed'];
const deck = 'Deck\n99 Forest\nCommander\n1 Rhys the Redeemed';

for (const transport of transports) test(`pregame concessions preserve the Commander lifecycle (${transport})`, { timeout: 240000 }, async t => {
  const profile = testProfile('pregame-concession');
  const clients = [];
  const disconnected = new Set();
  const evidence = { transport, cases: [] };
  let latest;
  async function states() {
    latest = await Promise.all(clients.map((client, seat) => disconnected.has(seat) ? null : client.request('matchState')));
    for (const state of latest) assert.notEqual(state?.status, 'error', state?.error);
    return latest;
  }
  async function until(check, message) {
    return waitFor(async () => check(await states()), message);
  }
  async function lifecycleCase(name, execute) {
    await t.test(name, async () => {
      try { await execute(); }
      catch (error) {
        evidence.failure = { message: error.message, stack: error.stack, snapshots: latest };
        throw error;
      }
    });
  }
  async function reply(seat, state, answer) {
    await clients[seat].request('matchAction', { sessionId: state.id, promptId: state.prompt.id, ...answer });
    await until(current => current[seat].prompt?.id !== state.prompt.id, 'The answered pregame decision did not advance');
  }
  async function startingChoice() {
    const current = await until(current => current.every(state => state?.players?.length === 4)
      && current.some(state => state.prompt?.inputType === 'InputSelectEntitiesFromList' && state.prompt.playerChoices.length === 4)
      && current, 'No starting-player choice was offered');
    assert.ok(current.every(state => state.turn === 0 && state.phaseKey === 'PREGAME'));
    assert.equal(new Set(current.map(state => state.viewerId)).size, 4, 'All four human seats must have distinct views');
    const seat = current.findIndex(state => state.prompt?.inputType === 'InputSelectEntitiesFromList');
    return { seat, state: current[seat] };
  }
  async function keepHand(firstSeat = 0) {
    const choice = await startingChoice();
    await reply(choice.seat, choice.state, { action: 'player', playerId: choice.state.players[firstSeat].id });
    const current = await until(current => current.some(state => state.prompt?.inputType === 'InputConfirmMulligan'
      && state.prompt.okEnabled && state.prompt.cancelEnabled) && current, 'No keep-hand choice was offered');
    const seat = current.findIndex(state => state.prompt?.inputType === 'InputConfirmMulligan' && state.prompt.okEnabled);
    assert.ok(current.every(state => state.turn === 0));
    return { seat, state: current[seat] };
  }
  async function firstMain(eliminated = []) {
    const answered = new Set();
    const current = await until(async current => {
      const connected = current.filter(Boolean);
      if (!connected.length || !connected.every(state => state.players.length === 4)) return false;
      for (const seat of eliminated) {
        assert.equal(connected[0].players.find(player => player.seat === seat + 1).eliminated, true);
        if (current[seat]) assert.equal(current[seat].prompt, null, 'A conceded seat must not receive another decision');
      }
      if (connected.some(state => state.turn > 0 && state.phaseKey === 'MAIN1'
          && state.prompt?.inputType === 'InputPassPriority' && state.prompt.okEnabled)) return current;
      for (let seat = 0; seat < current.length; seat++) {
        const state = current[seat], prompt = state?.prompt;
        if (eliminated.includes(seat) || !prompt || answered.has(prompt.id)) continue;
        assert.notEqual(state.status, 'finished', 'The remaining players must continue into their first turn');
        let answer;
        if (prompt.inputType === 'InputSelectEntitiesFromList' && prompt.playerChoices.length) {
          assert.ok(prompt.playerChoices.every(id => !state.players.find(player => player.id === id).eliminated),
            'A replacement starting-player choice must exclude eliminated seats');
          const playerId = prompt.playerChoices.find(id => !state.players.find(player => player.id === id).eliminated);
          assert.notEqual(playerId, undefined, 'The replacement starting-player choice must offer a surviving player');
          answer = { action: 'player', playerId };
        } else if (prompt.okEnabled) answer = { action: 'ok' };
        else if (prompt.kind === 'choice') answer = { choices: Array.from({ length: prompt.min }, (_, index) => index) };
        if (!answer) continue;
        // Never replay a rejected command: a stale or unusable opening decision
        // is a regression, not a reason to retry until the test happens to pass.
        await reply(seat, state, answer);
        answered.add(prompt.id);
      }
      return false;
    }, 'Surviving players did not reach a usable first main phase');
    assert.equal(current.find(Boolean).players.filter(player => !player.eliminated).length, 4 - eliminated.length);
    return current;
  }
  async function concede(seat, sessionId, record) {
    record.concessions.push(seat);
    await clients[seat].request('matchConcede', { sessionId });
  }
  async function finish(winner, sessionId, record) {
    const current = await states();
    for (let seat = 0; seat < clients.length; seat++) {
      if (seat !== winner && !current.find(Boolean).players.find(player => player.seat === seat + 1).eliminated) {
        await concede(seat, sessionId, record);
      }
    }
    const finished = await until(current => current.filter(Boolean).every(state => state.status === 'finished' && state.result) && current,
      'Pregame concessions did not produce a normal game result');
    for (let seat = 0; seat < clients.length; seat++) {
      if (disconnected.has(seat)) continue;
      assert.equal(finished[seat].result, seat === winner ? 'Victory' : 'Defeat');
      assert.equal(finished[seat].prompt, null, 'A finished table must clear every decision');
      assert.equal(finished[seat].players.filter(player => !player.eliminated).length, 1);
    }
    record.results = finished.map(state => state?.result ?? null);
  }
  async function returnAndRematch(record) {
    await clients[0].request('multiplayerReturn');
    for (const client of clients) {
      const lobby = await client.request('multiplayerState');
      assert.equal(lobby.matchActive, false);
      assert.ok(lobby.slots.every(slot => !slot.ready && slot.deck && slot.type !== 'OPEN'), 'Return preserves decks and resets all readiness');
      assert.equal(await client.request('matchState'), null);
      await assert.rejects(client.request('matchAction', { sessionId: record.sessionId, promptId: record.promptId, action: 'ok' }), /No active match/);
    }
    await assert.rejects(clients[0].request('multiplayerStart'), /ready up/);
    for (const client of clients) await client.request('multiplayerReady', { ready: true });
    assert.equal((await clients[0].request('multiplayerState')).canStart, true);
    await clients[0].request('multiplayerStart');
    const rematch = await firstMain();
    assert.notEqual(rematch[0].id, record.sessionId, 'The next game must have a new decision scope');
    await assert.rejects(clients[0].request('matchAction', { sessionId: record.sessionId, promptId: record.promptId, action: 'ok' }), /match is no longer active/);
    await finish(0, rematch[0].id, { concessions: [] });
    await clients[0].request('multiplayerReturn');
    record.rematchReachedMain = true;
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
    const deckIds = [];
    for (let seat = 0; seat < 4; seat++) {
      await clients[seat].request('import', { name: `Pregame seat ${seat + 1}`, format: 'Commander', text: deck });
      deckIds.push((await clients[seat].request('save')).id);
    }
    const lobby = await clients[0].request('multiplayerHost', { format: 'Commander', playerCount: 4, autoPortForward: false, transport });
    assert.equal(lobby.error, null);
    assert.equal(lobby.portMapping, 'disabled');
    const port = new URL(`http://${lobby.addresses[0].url}`).port;
    for (const guest of clients.slice(1)) await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    for (let seat = 0; seat < 4; seat++) await clients[seat].request('multiplayerSelectDeck', { deckId: deckIds[seat] });

    for (const [stage, rapid, leave] of [['starting-player', false], ['keep-hand', false], ['starting-player', true], ['keep-hand', true], ['keep-hand', false, true]]) {
      await lifecycleCase(`${stage}: ${leave ? 'a guest leaves during its decision and three survivors continue' : rapid ? 'three rapid guest concessions leave the host victorious' : 'the unanswered decision owner concedes and three survivors continue'}`, async () => {
        for (const client of clients) await client.request('multiplayerReady', { ready: true });
        await clients[0].request('multiplayerStart');
        const decision = await (stage === 'starting-player' ? startingChoice() : keepHand(leave ? 1 : 0));
        if (stage === 'keep-hand') assert.equal(decision.seat, leave ? 1 : 0, 'The intended player must own the pending keep-hand decision');
        const record = { stage, rapid, leave: Boolean(leave), owner: decision.seat, sessionId: decision.state.id, promptId: decision.state.prompt.id, concessions: [] };
        evidence.cases.push(record);
        if (rapid) {
          // Reproduce the original three-guest sequence without completing an
          // opening prompt or waiting between concessions.
          for (let seat = 1; seat < 4; seat++) await concede(seat, decision.state.id, record);
          await finish(0, decision.state.id, record);
          assert.equal(latest[0].turn, 0, 'Rapid concessions must finish before the opening decisions complete');
        } else {
          if (leave) {
            assert.equal(decision.seat, 1, 'The guest must own the pending keep-hand decision');
            const left = await clients[1].request('multiplayerClose');
            assert.equal(left.mode, 'idle');
            disconnected.add(1);
            const departed = (await clients[0].request('multiplayerState')).slots[1];
            assert.equal(departed.type, 'OPEN');
            assert.equal(departed.ready, false);
            assert.equal(departed.deck, null, 'Leaving revokes the guest seat and its deck');
          } else await concede(decision.seat, decision.state.id, record);
          await firstMain([decision.seat]);
          record.survivorsReachedMain = true;
          const winner = decision.seat === 1 ? 2 : 1;
          await finish(winner, decision.state.id, record);
        }
        if (leave) {
          await clients[0].request('multiplayerReturn');
          await clients[1].request('multiplayerJoin', { address: `127.0.0.1:${port}` });
          disconnected.delete(1);
          await clients[1].request('multiplayerSelectDeck', { deckId: deckIds[1] });
        } else {
          await assert.rejects(clients[decision.seat].request('matchAction', {
            sessionId: decision.state.id, promptId: decision.state.prompt.id, action: 'ok'
          }), /choice has changed/);
        }
        await returnAndRematch(record);
      });
      // Avoid cascading failures from attempting another match on a broken game.
      if (evidence.cases.at(-1)?.rematchReachedMain !== true) break;
    }
    if (transport === 'local' && evidence.cases.length === 5 && evidence.cases.every(record => record.rematchReachedMain)) {
      await lifecycleCase('the sole human concedes an opening decision against AI, then starts a playable new game', async () => {
        for (const guest of clients.slice(1).reverse()) await guest.close();
        clients.splice(1);
        await clients[0].request('multiplayerClose');
        await clients[0].request('matchStart', { opponents: ['green', 'red', 'green'] });
        const [opening] = await until(current => current[0]?.prompt && current, 'The solo match did not offer an opening decision');
        assert.equal(opening.turn, 0);
        await clients[0].request('matchConcede', { sessionId: opening.id });
        const [finished] = await until(current => current[0]?.status === 'finished' && current[0].result && current,
          'Solo opening concession did not finish');
        assert.equal(finished.result, 'Defeat');
        assert.equal(finished.prompt, null);
        await clients[0].request('matchStart', { opponents: ['green', 'red', 'green'] });
        const [rematch] = await firstMain();
        assert.notEqual(rematch.id, opening.id);
        await clients[0].request('matchConcede', { sessionId: rematch.id });
        evidence.soloOpeningConcession = { result: finished.result, rematchReachedMain: true };
      });
    }
  } catch (error) {
    evidence.failure = { message: error.message, stack: error.stack, snapshots: latest };
    throw error;
  } finally {
    evidence.snapshots = latest;
    fs.writeFileSync(path.join(profile, 'pregame-concession.json'), JSON.stringify(evidence, null, 2) + '\n');
    console.log(`Pregame concession evidence: ${path.join(profile, 'pregame-concession.json')}`);
    for (let seat = clients.length - 1; seat >= 0; seat--) if (!disconnected.has(seat)) await clients[seat].close();
  }
});
