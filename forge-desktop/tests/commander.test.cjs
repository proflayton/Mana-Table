const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { testProfile, startEngine, ready, submitMatchAction } = require('./support/engine.cjs');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

test('Commander setup validates detached leaders, command-zone casting, and Toph earthbending', { timeout: 180000 }, async () => {
  const profile = testProfile('commander');
  const engine = startEngine(profile);
  let triggerEvidence;
  try {
    await ready(engine);
    const toph = await engine.request('import', { name: 'Toph setup regression', format: 'Commander',
      text: 'Deck\n34 Forest\n33 Mountain\n31 Plains\n1 Toph, Greatest Earthbender\n1 Toph, the First Metalbender' });
    const prepared = await engine.request('matchSetup');
    assert.equal(prepared.setup.problem, null);
    assert.equal(prepared.setup.startingLife, 40);
    assert.deepEqual(prepared.setup.commanders, ['Toph, the First Metalbender']);
    const wrong = prepared.setup.commanderChoices.find(card => card.name === 'Toph, Greatest Earthbender');
    assert.equal(wrong.valid, false, 'White cards must fail red/green commander color identity');
    assert.ok((await engine.request('matchSetup', { commanderId: wrong.id })).setup.problem);
    await assert.rejects(engine.request('matchStart', { commanderId: wrong.id }), /color|identity/i);
    await assert.rejects(engine.request('matchStart', { commanderId: 'not-in-deck' }), /from this deck/);
    await assert.rejects(engine.request('matchStart', { deckId: 'old-deck' }), /deck changed/i);
    assert.deepEqual((await engine.request('snapshot')).deck, toph.deck, 'Setup must not move cards in the saved deck');

    const saved = await engine.request('import', { name: 'Command-zone casting', format: 'Commander', text: 'Deck\n99 Mountain\n1 Rograkh, Son of Rohgahh' });
    const launch = await engine.request('matchSetup');
    assert.equal(launch.setup.problem, null);
    let state = await engine.request('matchStart', { opponent: 'green', commanderId: launch.setup.commanderId });
    let sawCommanders = false, cast = false, oldPrompt;
    const deadline = Date.now() + 65000;
    while (Date.now() < deadline) {
      state = await engine.request('matchState');
      assert.notEqual(state.status, 'error', state.error);
      const p = state.prompt;
      if (!p || p.id === oldPrompt) { await sleep(25); continue; }
      const human = state.players.find(player => player.human);
      const ai = state.players.find(player => !player.human);
      const zone = (player, name) => player.zones.find(zone => zone.name === name);
      if (!sawCommanders) {
        assert.equal(state.format, 'Commander');
        assert.equal(human.life, 40); assert.equal(ai.life, 40);
        assert.equal(zone(human, 'Library').count + zone(human, 'Hand').count, 99);
        assert.equal(zone(ai, 'Library').count + zone(ai, 'Hand').count, 99);
        assert.ok(zone(human, 'Command').cards.some(card => card.name === 'Rograkh, Son of Rohgahh'));
        assert.ok(zone(ai, 'Command').cards.some(card => card.name === 'Goreclaw, Terror of Qal Sisma'));
        sawCommanders = true;
      }
      assert.equal(zone(ai, 'Hand').cards.length, 0);
      if (zone(human, 'Battlefield').cards.some(card => card.name === 'Rograkh, Son of Rohgahh')) { cast = true; break; }
      const answer = { sessionId: state.id, promptId: p.id };
      if (p.kind === 'choice') answer.choices = Array.from({ length: p.min }, (_, index) => index);
      else if (p.kind === 'reveal') answer.action = 'ack';
      else if (p.inputType === 'InputPassPriority') {
        const commander = zone(human, 'Command').cards.find(card => card.name === 'Rograkh, Son of Rohgahh' && card.selectable);
        if (commander) { answer.action = 'card'; answer.key = commander.key; }
        else answer.action = 'ok';
      } else if (p.okEnabled) answer.action = 'ok';
      else throw new Error('Unexpected Commander prompt: ' + JSON.stringify(p));
      oldPrompt = p.id;
      await engine.request('matchAction', answer);
    }
    assert.ok(cast, 'The commander must resolve onto the battlefield from the command zone');
    assert.deepEqual((await engine.request('snapshot')).deck, saved.deck);
    await engine.request('matchConcede', { sessionId: state.id });

    state = await engine.request('matchStart', { opponent: 'red' });
    const redDeadline = Date.now() + 10000;
    while (!state.prompt && Date.now() < redDeadline) { await sleep(25); state = await engine.request('matchState'); }
    assert.notEqual(state.status, 'error', state.error);
    assert.ok(state.players.find(player => !player.human).zones.find(zone => zone.name === 'Command').cards.some(card => card.name === 'Torbran, Thane of Red Fell'));
    await engine.request('matchConcede', { sessionId: state.id });

    // A mandatory end-step trigger must survive the UI ability adapter. Such abilities
    // have canPlay=false because they are triggered, not activated by clicking a card.
    // A passive local human prevents a competitive AI ending the game before a
    // shuffled opening can supply all three colors. Both seats use this saved deck;
    // the guest never casts its commander or attacks. No deal or game state is injected.
    const assigned = await engine.request('import', { name: 'Toph trigger regression', format: 'Commander',
      text: 'Deck\n33 Plains\n33 Mountain\n33 Forest\nCommander\n1 Toph, the First Metalbender' });
    assert.equal((await engine.request('matchSetup')).setup.needsCommander, false);
    const triggerDeck = await engine.request('save');
    const lobby = await engine.request('multiplayerHost', { format: 'Commander', playerCount: 2, autoPortForward: false, transport: 'local' });
    assert.equal(lobby.error, null);
    const { connectionId } = await engine.request('multiplayerLocalJoin');
    const guest = { request: (method, args = {}) => engine.request(method, { ...args, connectionId }) };
    const clients = [engine, guest];
    for (const client of clients) {
      await client.request('multiplayerSelectDeck', { deckId: triggerDeck.id });
      await client.request('multiplayerReady', { ready: true });
    }
    await engine.request('multiplayerStart');
    triggerEvidence = { transport: 'local', deckId: triggerDeck.id, actions: [], actionCount: 0,
      commanderSelected: false, commanderOnStack: false, commanderResolved: false, endStepTrigger: false, targetId: null };
    const answered = new Map(), waiting = new Map();
    let bent;
    const triggerDeadline = Date.now() + 90000;
    while (Date.now() < triggerDeadline && !bent) {
      const snapshots = await Promise.all(clients.map(client => client.request('matchState')));
      triggerEvidence.snapshots = snapshots;
      for (let seat = 0; seat < clients.length; seat++) {
        state = snapshots[seat];
        if (!state?.players?.length) continue;
        assert.notEqual(state.status, 'error', state.error);
        assert.ok(!state.result && state.status !== 'finished', `Trigger encounter ended before earthbending: ${JSON.stringify(state)}`);
        const human = state.players.find(player => player.id === state.viewerId);
        const zone = name => human.zones.find(zone => zone.name === name).cards;
        for (const opponent of state.players.filter(player => player.id !== state.viewerId)) {
          assert.deepEqual(opponent.zones.find(zone => zone.name === 'Hand').cards, []);
          assert.deepEqual(opponent.zones.find(zone => zone.name === 'Library').cards, []);
        }
        if (seat === 0) {
          const top = zone('Battlefield').find(card => card.name === 'Toph, the First Metalbender');
          if (top) triggerEvidence.commanderResolved = true;
          for (const item of state.stack) if (item.name === 'Toph, the First Metalbender') {
            if (!item.ability) triggerEvidence.commanderOnStack = true;
            else if (/earthbend\s+2\b/i.test(item.text) && state.activePlayerId === state.viewerId && state.phaseKey === 'END_OF_TURN') {
              triggerEvidence.endStepTrigger = true;
            }
          }
          bent = zone('Battlefield').find(card => card.visualId === triggerEvidence.targetId
            && card.type.includes('Land') && card.type.includes('Creature') && card.counters['+1/+1'] === 2);
          if (bent) break;
        }
        const p = state.prompt;
        if (!p || answered.get(seat) === p.id) continue;
        if (p.kind === 'input' && p.inputType === 'InputPassPriority' && !p.okEnabled) continue;
        const answer = {};
        let selectedCommander = false, selectedTarget;
        if (p.playerChoices?.length) {
          assert.ok(p.playerChoices.includes(snapshots[0].viewerId));
          answer.action = 'player'; answer.playerId = snapshots[0].viewerId;
        } else if (p.kind === 'choice') answer.choices = Array.from({ length: Math.max(p.min, Math.min(1, p.max)) }, (_, index) => index);
        else if (p.kind === 'reveal') answer.action = 'ack';
        else if (seat === 0 && p.inputType === 'InputPassPriority' && state.activePlayerId === state.viewerId
            && state.phaseKey === 'MAIN1' && !state.stack.length) {
          const lands = zone('Hand').filter(card => card.selectable && card.type.includes('Land'));
          const missing = ['Plains', 'Mountain', 'Forest'].find(type => !zone('Battlefield').some(card => card.type.includes(type))
            && lands.some(card => card.type.includes(type)));
          const commander = zone('Command').find(card => card.selectable && card.name === 'Toph, the First Metalbender');
          const card = lands.find(card => missing && card.type.includes(missing)) || lands[0] || commander;
          if (card) {
            answer.action = 'card'; answer.key = card.key;
            selectedCommander = card === commander;
          } else answer.action = 'ok';
        } else if (seat === 0 && p.inputType === 'InputSelectTargets') {
          assert.ok(triggerEvidence.commanderResolved, 'Earthbend targeting follows the actual commander resolution');
          assert.equal(state.activePlayerId, state.viewerId);
          assert.equal(state.phaseKey, 'END_OF_TURN');
          const target = zone('Battlefield').find(card => card.selectable && !card.highlighted && card.type.includes('Land'));
          if (target) { answer.action = 'card'; answer.key = target.key; selectedTarget = target.visualId; }
          else if (p.okEnabled) answer.action = 'ok';
        } else if (p.okEnabled) answer.action = 'ok';
        else {
          // The passive guest also services normal maximum-hand-size cleanup.
          const discard = zone('Hand').find(card => card.selectable && !card.highlighted && card.type.includes('Land'));
          if (discard) { answer.action = 'card'; answer.key = discard.key; }
        }
        if (!answer.action && !answer.choices) {
          const previous = waiting.get(seat);
          if (previous?.id !== p.id) waiting.set(seat, { id: p.id, since: Date.now() });
          else assert.ok(Date.now() - previous.since < 5000, `Unusable trigger decision: ${JSON.stringify(p)}`);
          continue;
        }
        waiting.delete(seat);
        triggerEvidence.actions.push({ seat, turn: state.turn, phase: state.phaseKey, prompt: p, answer,
          hand: zone('Hand'), field: zone('Battlefield'), stack: state.stack });
        if (triggerEvidence.actions.length > 120) triggerEvidence.actions.shift();
        triggerEvidence.actionCount++;
        if (await submitMatchAction(clients[seat], state, answer)) {
          answered.set(seat, p.id);
          if (selectedCommander) triggerEvidence.commanderSelected = true;
          if (selectedTarget) triggerEvidence.targetId = selectedTarget;
        }
      }
      if (!bent) await sleep(15);
    }
    assert.ok(bent, 'Toph must earthbend a land into a creature with two counters at the end step');
    assert.ok(triggerEvidence.commanderSelected && triggerEvidence.commanderOnStack && triggerEvidence.commanderResolved,
      'Toph must be cast from the command zone and resolve normally');
    assert.ok(triggerEvidence.endStepTrigger, 'The mandatory Earthbend2 ability must reach the stack during its controller\'s end step');
    assert.equal(bent.counters['+1/+1'], 2);
    assert.equal(bent.power, 2); assert.equal(bent.toughness, 2);
    triggerEvidence.bent = bent;
    assert.deepEqual((await engine.request('snapshot')).deck, assigned.deck);
    await guest.request('matchConcede', { sessionId: state.id });
    console.log('Commander: color identity, 40 life, 99-card libraries, both AI decks, command-zone casting, earthbending, unchanged saved decks.');
  } catch (error) {
    if (triggerEvidence) triggerEvidence.failure = { message: error.message, stack: error.stack };
    throw error;
  } finally {
    try {
      if (triggerEvidence) fs.writeFileSync(path.join(profile, 'commander-trigger.json'), JSON.stringify(triggerEvidence, null, 2));
    } finally { await engine.close(); }
  }
});
