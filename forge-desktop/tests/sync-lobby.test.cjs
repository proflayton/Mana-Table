const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { testProfile, startEngine, ready, submitMatchAction } = require('./support/engine.cjs');

test('a mistyped invite preserves the existing seat and its resumable connection', { timeout: 90000 }, async () => {
  const clients = [startEngine(testProfile('sync-lobby-host')), startEngine(testProfile('sync-lobby-guest'))];
  const [host, guest] = clients;
  try {
    await Promise.all(clients.map(ready));
    const lobby = await host.request('multiplayerHost', { format: 'Commander', playerCount: 2, autoPortForward: false });
    assert.match(lobby.startProblem, /1\/2/);
    const port = new URL(`http://${lobby.addresses[0].url}`).port;
    await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    await assert.rejects(guest.request('multiplayerJoin', { address: 'MT1-BROKEN' }), /incomplete or mistyped/);
    await assert.rejects(guest.request('multiplayerJoin', { address: 'not-an-endpoint' }), /host:port/);
    assert.equal((await guest.request('multiplayerState')).mode, 'joined');
    assert.equal((await host.request('multiplayerState')).slots.filter(slot => slot.type !== 'OPEN').length, 2);
    assert.equal((await guest.request('multiplayerReconnect')).mode, 'joined');
    const idle = await guest.request('multiplayerClose');
    assert.equal(idle.hosting, false);
    assert.equal((await host.request('multiplayerState')).slots[1].type, 'OPEN');
    await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
    assert.equal((await guest.request('multiplayerState')).mode, 'joined');
  } finally { await Promise.all(clients.map(client => client.close())); }
});

test('AI seat choices follow lobby format, local guest authority, and table identity', { timeout: 90000 }, async () => {
  const host = startEngine(testProfile('sync-lobby-ai-setup'));
  try {
    await ready(host);
    const opened = await host.request('multiplayerHost', { format: 'Constructed', playerCount: 2, transport: 'local', autoPortForward: false });
    const constructed = await host.request('multiplayerSetSeat', { tableId: opened.tableId, seat: 1, type: 'AI', opponentId: 'red' });
    assert.equal(constructed.slots[1].type, 'AI');
    assert.equal(constructed.slots[1].deck, 'red');
    assert.equal(constructed.slots[1].ready, true);
    await assert.rejects(host.request('multiplayerSetSeat', { tableId: opened.tableId, seat: 1, type: 'AI', opponentId: 'preset:unknown' }), /Unknown opponent/);
    assert.equal((await host.request('multiplayerState')).slots[1].opponentId, 'red');
    const changed = await host.request('multiplayerConfigure', { tableId: opened.tableId, format: 'Commander', playerCount: 4 });
    assert.equal(changed.slots[1].type, 'OPEN', 'Changing format clears an incompatible AI choice');
    assert.equal(changed.slots[1].opponentId, null);
    assert.equal(changed.slots[1].ready, false);
    await host.request('multiplayerSetSeat', { tableId: opened.tableId, seat: 1, type: 'AI', opponentId: 'green' });
    const { connectionId } = await host.request('multiplayerLocalJoin');
    const guest = await host.request('multiplayerState', { connectionId });
    assert.equal(guest.slots.find(slot => slot.local).index, 2);
    await assert.rejects(host.request('multiplayerSetSeat', { connectionId, tableId: opened.tableId, seat: 1, type: 'OPEN' }), /Only the host/);
    await assert.rejects(host.request('multiplayerConfigure', { connectionId, tableId: opened.tableId, format: 'Commander', playerCount: 3 }), /Only the host/);
    await assert.rejects(host.request('multiplayerSetSeat', { tableId: opened.tableId, seat: 2, type: 'AI' }), /connected player occupies/);
    const newTable = await host.request('multiplayerHost', { format: 'Commander', playerCount: 4, transport: 'local', autoPortForward: false });
    assert.notEqual(newTable.tableId, opened.tableId);
    await assert.rejects(host.request('multiplayerSetSeat', { tableId: opened.tableId, seat: 1, type: 'AI' }), /table has changed/);
    assert.equal((await host.request('multiplayerState')).slots[1].type, 'OPEN');
  } finally { await host.close(); }
});

test('a host can remove the unused fourth seat and start three-human Commander', { timeout: 120000 }, async () => {
  const profile = testProfile('sync-lobby-three-humans');
  const clients = [0, 1, 2].map(index => startEngine(path.join(profile, `human-${index}`)));
  const [host, firstGuest, secondGuest] = clients;
  let latest = [];
  try {
    await Promise.all(clients.map(ready));
    const decks = [];
    for (let i = 0; i < clients.length; i++) decks.push(await clients[i].request('import', {
      name: `Three-player deck ${i + 1}`, format: 'Commander', text: 'Commander\n1 Rhys the Redeemed\nDeck\n99 Forest'
    }));
    const opened = await host.request('multiplayerHost', { format: 'Commander', playerCount: 4, autoPortForward: false });
    const tableId = opened.tableId;
    const address = opened.addresses.find(item => item.url.startsWith('127.0.0.1:')).url;
    await firstGuest.request('multiplayerJoin', { address });
    await secondGuest.request('multiplayerJoin', { address });
    for (let i = 0; i < clients.length; i++) {
      await clients[i].request('multiplayerSelectDeck', { deckId: decks[i].id });
      await clients[i].request('multiplayerReady', { ready: true });
    }
    const waiting = await host.request('multiplayerState');
    assert.equal(waiting.slots[3].type, 'OPEN');
    assert.equal(waiting.canStart, false);
    assert.match(waiting.startProblem, /3\/4/);
    await assert.rejects(firstGuest.request('multiplayerConfigure', { tableId, format: 'Commander', playerCount: 3 }), /Only the host/);
    await assert.rejects(host.request('multiplayerConfigure', { tableId, format: 'Commander', playerCount: 2 }), /connected player occupies/);
    await assert.rejects(host.request('multiplayerConfigure', { tableId: 'stale-table', format: 'Commander', playerCount: 3 }), /table has changed/);
    await assert.rejects(host.request('multiplayerConfigure', { format: 'Commander', playerCount: 3 }), /table has changed/);
    assert.deepEqual((await host.request('multiplayerState')).slots, waiting.slots,
      'Rejected table changes preserve every connected seat, selected deck and ready flag');
    const smaller = await host.request('multiplayerConfigure', { tableId, format: 'Commander', playerCount: 3 });
    assert.equal(smaller.slots.length, 3);
    assert.ok(smaller.slots.every(slot => slot.type !== 'OPEN' && slot.type !== 'AI' && slot.deck && !slot.ready),
      'Removing an unused seat preserves human decks and asks everyone to ready again');
    for (const client of clients) await client.request('multiplayerReady', { ready: true });
    assert.equal((await host.request('multiplayerState')).canStart, true);
    await host.request('multiplayerStart');
    const answered = new Set(), deadline = Date.now() + 45000;
    let playable = false;
    while (Date.now() < deadline) {
      latest = await Promise.all(clients.map(client => client.request('matchState')));
      for (const state of latest) {
        assert.notEqual(state?.status, 'error', state?.error);
        if (!state?.players?.length) continue;
        assert.equal(state.playerCount, 3);
        assert.equal(state.players.length, 3);
        for (const player of state.players) {
          const hand = player.zones.find(zone => zone.name === 'Hand');
          assert.equal(hand.cards.length, player.id === state.viewerId ? hand.count : 0);
        }
      }
      if (latest.every(state => state?.players?.length === 3)
          && latest.some(state => state.phaseKey === 'MAIN1' && state.activePlayerId === state.viewerId
            && state.prompt?.inputType === 'InputPassPriority' && state.prompt.okEnabled)) { playable = true; break; }
      for (let i = 0; i < clients.length; i++) {
        const state = latest[i], prompt = state?.prompt;
        if (!prompt || answered.has(prompt.id)) continue;
        const answer = prompt.kind === 'choice' ? { choices: prompt.choices.slice(0, prompt.min).map(choice => choice.index) }
          : prompt.kind === 'reveal' ? { action: 'ack' }
          : prompt.playerChoices?.length ? { action: 'player', playerId: prompt.playerChoices[0] }
          : prompt.okEnabled ? { action: 'ok' } : null;
        if (answer && await submitMatchAction(clients[i], state, answer)) answered.add(prompt.id);
      }
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.ok(playable, 'Three real human seats must reach a usable first main phase without an AI');
    assert.equal(new Set(latest.map(state => state.viewerId)).size, 3);
    for (const client of clients) await client.request('matchConcede', { sessionId: latest[0].id });
    fs.writeFileSync(path.join(profile, 'three-human-evidence.json'), JSON.stringify({ lobby: smaller, snapshots: latest }, null, 2));
  } catch (error) {
    fs.writeFileSync(path.join(profile, 'three-human-failure.json'), JSON.stringify({ error: error.message, snapshots: latest }, null, 2));
    throw error;
  } finally { await Promise.all(clients.map(client => client.close())); }
});

test('three real friends can play four-player Commander with a host-managed AI seat', { timeout: 180000 }, async () => {
  const profile = testProfile('sync-lobby-ai');
  const clients = [0, 1, 2].map(index => startEngine(path.join(profile, `human-${index}`)));
  const [host, firstGuest, secondGuest] = clients;
  const sourceDecks = [], actions = [];
  let latest = [], stage = 'setup', tableId;
  const zone = (player, name) => player.zones.find(item => item.name === name);
  const seat = (index, type, opponentId) => host.request('multiplayerSetSeat', { tableId, seat: index, type, opponentId });
  async function snapshots() {
    latest = await Promise.all(clients.map(client => client.request('matchState')));
    for (const state of latest) {
      assert.notEqual(state?.status, 'error', state?.error);
      if (!state?.players?.length) continue;
      assert.equal(state.players.length, 4);
      assert.equal(state.players.filter(player => player.human).length, 1, 'The legacy human flag identifies only this local viewer');
      assert.ok(state.players.find(player => player.id === state.viewerId)?.human, 'Only a human seat owns a connection');
      for (const player of state.players) {
        const hand = zone(player, 'Hand');
        assert.equal(hand.cards.length, player.id === state.viewerId ? hand.count : 0,
          'Every human sees its own hand and no opponent hand, including the AI');
        assert.equal(zone(player, 'Library').cards.length, 0, 'Library contents remain private');
      }
      if (state.prompt && state.decisionOwnerId != null) assert.equal(state.decisionOwnerId, state.viewerId);
    }
    return latest;
  }
  async function until(check, message, timeout = 45000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      const current = await snapshots();
      if (check(current)) return current;
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    throw new Error(message);
  }
  function safeAnswer(state) {
    const prompt = state.prompt;
    if (prompt.kind === 'choice') return { choices: prompt.choices.slice(0, prompt.min).map(choice => choice.index) };
    if (prompt.kind === 'reveal') return { action: 'ack' };
    if (prompt.kind === 'number') return { value: prompt.min };
    if (prompt.playerChoices?.length) return { action: 'player', playerId: prompt.playerChoices[0] };
    if (prompt.okEnabled) return { action: 'ok' };
    if (prompt.inputType === 'InputSelectCardsFromList') {
      const viewer = state.players.find(player => player.id === state.viewerId);
      const card = zone(viewer, 'Hand').cards.find(card => card.selectable && !card.highlighted);
      assert.ok(card, 'The required cleanup discard exposes a selectable card from this human hand');
      return { action: 'card', key: card.key };
    }
    return null;
  }
  try {
    await Promise.all(clients.map(ready));
    for (let i = 0; i < clients.length; i++) {
      const saved = await clients[i].request('import', { name: `Friend ${i + 1} Commander`, format: 'Commander',
        text: 'Commander\n1 Rhys the Redeemed\nDeck\n99 Forest' });
      sourceDecks.push(saved);
    }
    const opened = await host.request('multiplayerHost', { format: 'Commander', playerCount: 4, autoPortForward: false, transport: 'mixed' });
    tableId = opened.tableId;
    assert.ok(opened.aiOpponents.some(choice => choice.id === 'green'));
    const initial = await seat(1, 'AI');
    assert.equal(initial.slots[1].type, 'AI');
    assert.equal(initial.slots[1].opponentId, initial.aiOpponents[0].id, 'Adding AI selects a compatible default');
    assert.equal(initial.slots[1].ready, true);
    assert.ok(initial.slots[1].deck);
    assert.equal(initial.canStart, false, 'AI does not implicitly ready the host or fill other open seats');
    const replaced = await seat(1, 'AI', 'red');
    assert.equal(replaced.slots[1].opponentId, 'red');
    assert.equal(replaced.slots[1].deck, 'Cinder Commander');
    const removed = await seat(1, 'OPEN');
    assert.deepEqual({ type: removed.slots[1].type, deck: removed.slots[1].deck, ready: removed.slots[1].ready, opponentId: removed.slots[1].opponentId },
      { type: 'OPEN', deck: null, ready: false, opponentId: null });
    await assert.rejects(seat(1, 'AI', 'unknown'), /Unknown opponent/);
    await assert.rejects(seat(1.5, 'AI', 'green'), /Choose an open or AI seat/);
    await assert.rejects(seat(1, 'HUMAN'), /Choose AI or an open seat/);
    assert.equal((await host.request('multiplayerState')).slots[1].type, 'OPEN', 'Invalid choices leave the seat unchanged');
    const configured = await seat(1, 'AI', 'green');
    assert.equal(configured.slots[1].deck, 'Verdant Commander');
    const address = configured.addresses.find(item => item.url.startsWith('127.0.0.1:')).url;
    for (const guest of [firstGuest, secondGuest]) await guest.request('multiplayerJoin', { address });
    assert.equal((await firstGuest.request('multiplayerState')).slots.find(item => item.local).index, 2, 'A joining friend skips the AI seat');
    assert.equal((await secondGuest.request('multiplayerState')).slots.find(item => item.local).index, 3);
    await assert.rejects(firstGuest.request('multiplayerSetSeat', { tableId, seat: 1, type: 'OPEN' }), /Only the host/);
    await assert.rejects(seat(0, 'AI', 'green'), /other than the host/);
    await assert.rejects(seat(2, 'AI', 'green'), /connected player occupies/);
    await assert.rejects(seat(2, 'OPEN'), /connected player occupies/);
    await assert.rejects(host.request('multiplayerSetSeat', { tableId: 'old-table', seat: 1, type: 'OPEN' }), /table has changed/);
    for (let i = 0; i < clients.length; i++) {
      await clients[i].request('multiplayerSelectDeck', { deckId: sourceDecks[i].id });
      await clients[i].request('multiplayerReady', { ready: true });
    }
    const readyLobby = await seat(1, 'AI', 'green');
    assert.ok(readyLobby.canStart);
    assert.ok(readyLobby.slots.every(item => item.ready), 'AI changes preserve already-ready human seats');
    stage = 'first game';
    await host.request('multiplayerStart');
    let current = await until(states => states.every(state => state?.players?.length === 4), 'The mixed table did not publish all three human views');
    assert.equal(new Set(current.map(state => state.viewerId)).size, 3, 'The three real clients have distinct human viewers');
    const ai = current[0].players.find(player => player.seat === 2);
    assert.equal(ai.seat, 2, 'The AI retains its chosen seat between noncontiguous human seats');
    assert.match(ai.name, /AI/);
    assert.ok(current.every(state => state.viewerId !== ai.id), 'No connection is assigned to the AI controller');
    assert.equal(ai.life, 40);
    assert.equal(zone(ai, 'Command').cards[0].name, 'Goreclaw, Terror of Qal Sisma');
    const views = await host.request('testMatchViews');
    assert.equal(views.length, 3, 'AI controllers never receive a human decision endpoint');
    assert.deepEqual(views.map(view => view.viewerId).sort(), current.map(view => view.viewerId).sort());
    await assert.rejects(seat(1, 'OPEN'), /Return to the lobby/);
    const beforeReconnect = current[1];
    const reconnected = await firstGuest.request('multiplayerReconnect');
    assert.equal(reconnected.slots[1].type, 'AI');
    assert.equal(reconnected.slots[1].opponentId, 'green');
    current = await snapshots();
    assert.equal(current[1].viewerId, beforeReconnect.viewerId);
    assert.equal(current[1].id, beforeReconnect.id, 'Reconnect restores the same human seat at the mixed table');
    const answered = new Set(), deadline = Date.now() + 70000;
    while (Date.now() < deadline) {
      const aiTurn = current[0].activity.some(entry => entry.kind === 'turn' && entry.playerId === ai.id);
      if (aiTurn && current.some(state => state.prompt?.inputType === 'InputPassPriority'
          && state.activePlayerId === state.viewerId && state.turn > 1)) break;
      for (let i = 0; i < clients.length; i++) {
        const state = current[i];
        assert.notEqual(state.status, 'finished', 'The AI encounter must continue before concession');
        if (!state.prompt || answered.has(state.prompt.id)) continue;
        const answer = safeAnswer(state);
        if (!answer) continue;
        if (await submitMatchAction(clients[i], state, answer)) answered.add(state.prompt.id);
        actions.push({ viewerId: state.viewerId, turn: state.turn, phase: state.phaseKey, input: state.prompt.inputType, answer });
      }
      await new Promise(resolve => setTimeout(resolve, 15));
      current = await snapshots();
    }
    assert.ok(current[0].activity.some(entry => entry.kind === 'turn' && entry.playerId === ai.id), 'The AI takes its turn through the real engine');
    const firstTurn = current[0].activity.find(entry => entry.kind === 'turn');
    if (firstTurn?.playerId === current[0].viewerId) assert.ok(actions.some(event => event.phase === 'CLEANUP'
      && event.input === 'InputSelectCardsFromList' && event.answer.action === 'card'),
    'Passing the starting human turn exercises the mandatory cleanup discard before the AI turn');
    assert.ok(current.some(state => state.prompt?.inputType === 'InputPassPriority'
      && state.activePlayerId === state.viewerId && state.turn > 1), 'Humans regain a usable decision after AI play');
    for (const client of clients) await client.request('matchConcede', { sessionId: current[0].id });
    await until(states => states.every(state => state.status === 'finished' && !state.prompt), 'The game did not finish after all humans conceded');
    await host.request('multiplayerReturn');
    const returned = await Promise.all(clients.map(client => client.request('multiplayerState')));
    for (const lobby of returned) {
      assert.equal(lobby.matchActive, false);
      assert.equal(lobby.slots[1].type, 'AI');
      assert.equal(lobby.slots[1].opponentId, 'green');
      assert.equal(lobby.slots[1].ready, true, 'An AI with a deck stays ready for the next game');
      assert.ok(lobby.slots.filter(item => item.type !== 'AI').every(item => !item.ready && item.deck), 'Human decks remain selected and human readiness resets');
    }
    stage = 'rematch';
    for (const client of clients) await client.request('multiplayerReady', { ready: true });
    assert.equal((await host.request('multiplayerState')).canStart, true);
    const previousId = current[0].id;
    await host.request('multiplayerStart');
    current = await until(states => states.every(state => state?.players?.length === 4) && states.some(state => state.prompt), 'The mixed rematch did not publish a fresh decision');
    assert.notEqual(current[0].id, previousId);
    for (const client of clients) await client.request('matchConcede', { sessionId: current[0].id });
    await until(states => states.every(state => state.status === 'finished'), 'The rematch did not finish');
    await host.request('multiplayerReturn');
    const reopened = await seat(1, 'OPEN');
    assert.equal(reopened.slots[1].type, 'OPEN');
    assert.equal(reopened.canStart, false);
    assert.match(reopened.startProblem, /3\/4/);
    for (let i = 0; i < clients.length; i++) assert.deepEqual((await clients[i].request('snapshot')).deck, sourceDecks[i].deck,
      'Configuring and playing an AI seat never changes a human saved deck');
    fs.writeFileSync(path.join(profile, 'ai-seat-evidence.json'), JSON.stringify({ stage: 'complete', actions, snapshots: latest, lobby: reopened }, null, 2));
  } catch (error) {
    fs.writeFileSync(path.join(profile, 'ai-seat-failure.json'), JSON.stringify({ stage, error: error.message, actions, snapshots: latest }, null, 2));
    throw error;
  } finally { await Promise.all(clients.map(client => client.close())); }
});
