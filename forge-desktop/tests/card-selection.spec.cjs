const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');
const { startEngine, testProfile, ready, submitMatchAction } = require('./support/engine.cjs');
const fs = require('node:fs');
const path = require('node:path');

function selectionProfile(prefix) {
  const profile = testProfile(prefix);
  const preferencesFile = path.join(profile, 'engine-profile', 'preferences', 'forge.preferences');
  fs.mkdirSync(path.dirname(preferencesFile), { recursive: true });
  // The normal Forge preference removes presentation pauses during genuine
  // End Turn passes; priority, choices, shuffle and all game rules stay active.
  const contents = 'YIELD_SKIP_PHASE_DELAY=true\n';
  fs.writeFileSync(preferencesFile, contents);
  return { profile, preferencesFile, contents };
}

// Real, bounded setup: no shuffled-hand restart or injected cards. A passive
// opponent permits genuine draws; Ranger can return the earlier turn's Forest
// after Nantuko even when the four Nantukos were shuffled below every Forest.
async function prepareSelectionTable(host, guest, saved, observations, deadline) {
  await guest.request('import', { name: 'Passive selection opponent', format: 'Constructed', text: 'Deck\n60 Forest' });
  const opponent = await guest.request('save');
  const lobby = await host.request('multiplayerHost', { format: 'Constructed', playerCount: 2, autoPortForward: false });
  expect(lobby.error).toBeNull();
  expect(lobby.playerCount).toBe(2);
  expect(lobby.portMapping).toBe('disabled');
  const port = new URL(`http://${lobby.addresses[0].url.replace(/^https?:\/\//, '')}`).port;
  await guest.request('multiplayerJoin', { address: `127.0.0.1:${port}` });
  const clients = [host, guest];
  for (let seat = 0; seat < clients.length; seat++) {
    await clients[seat].request('multiplayerSelectDeck', { deckId: seat === 0 ? saved.id : opponent.id });
    await clients[seat].request('multiplayerReady', { ready: true });
  }
  const connected = await Promise.all(clients.map(client => client.request('multiplayerState')));
  expect(connected[0].mode).toBe('hosting');
  expect(connected[1].mode).toBe('joined');
  expect(connected[0].slots.map(slot => slot.type)).toEqual(['LOCAL', 'REMOTE']);
  expect(connected[1].slots.map(slot => slot.type)).toEqual(['REMOTE', 'LOCAL']);
  expect(connected.every(state => state.playerCount === 2 && state.slots.every(slot => slot.ready && slot.deck))).toBe(true);
  observations.connections = connected;
  await host.request('multiplayerStart');
  const answered = new Map(), waiting = new Map(), ownTurns = new Set();
  let firstForestTurn, retainedNantuko, mode = '', actions = 0;
  const describe = card => ({ name: card.name, visualId: card.visualId, key: card.key, selectable: card.selectable, highlighted: card.highlighted });
  const waitForReady = (seat, prompt) => {
    const previous = waiting.get(seat);
    if (previous?.id !== prompt.id) waiting.set(seat, { id: prompt.id, since: Date.now() });
    else expect(Date.now() - previous.since, `Unusable current setup decision: ${JSON.stringify({ seat, mode, prompt })}`).toBeLessThan(5000);
  };
  while (Date.now() < deadline && actions < 2000) {
    const states = await Promise.all(clients.map(client => client.request('matchState')));
    observations.lastStates = states;
    if (!states.every(state => state?.players?.length === 2 && Number.isInteger(state.viewerId))
        || new Set(states.map(state => state?.viewerId)).size !== 2) {
      await new Promise(resolve => setTimeout(resolve, 10));
      continue;
    }
    for (let seat = 0; seat < clients.length; seat++) {
      const state = states[seat];
      if (!state?.players?.length) continue;
      expect(state.status, state.error).not.toBe('error');
      expect(state.status, 'Passive setup must not end before the selection task').not.toBe('finished');
      if (state.turn === 1) {
        expect(state.activePlayerId).toBe(states[1].viewerId);
        observations.firstActivePlayerId = state.activePlayerId;
      }
      const self = state.players.find(player => player.id === state.viewerId);
      const zone = name => self.zones.find(item => item.name === name).cards;
      const libraryCount = self.zones.find(item => item.name === 'Library').count;
      const hand = zone('Hand'), field = zone('Battlefield'), p = state.prompt;
      for (const other of state.players.filter(player => player.id !== state.viewerId)) {
        expect(other.zones.find(item => item.name === 'Hand').cards).toEqual([]);
        expect(other.zones.find(item => item.name === 'Library').cards).toEqual([]);
      }
      if (seat === 0) {
        retainedNantuko ||= hand.find(card => card.name === 'Springheart Nantuko')?.visualId;
        if (state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1') ownTurns.add(state.turn);
        const forest = field.find(card => card.name === 'Forest');
        if (forest && firstForestTurn === undefined) firstForestTurn = state.turn;
        const pairIndex = hand.findIndex((card, index) => card.name === 'Springheart Nantuko' && hand[index + 1]?.name === 'Forest');
        if (mode === 'returning-forest' && ownTurns.size >= 2 && state.turn !== firstForestTurn
            && state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1'
            && p?.inputType === 'InputPassPriority' && p.okEnabled && !state.stack.length
            && !field.some(card => card.type.includes('Land')) && pairIndex >= 0 && hand[pairIndex + 1].selectable) {
          expect(observations.forestPlays).toHaveLength(1);
          expect(observations.forestPlays[0].turn).not.toBe(state.turn);
          if (self.landPlaysUsed !== undefined) expect(self.landPlaysUsed).toBe(0);
          if (observations.stressTail) {
            expect(libraryCount).toBe(0);
            expect(ownTurns.size).toBe(53);
          }
          observations.setup = { ownTurns: ownTurns.size, commands: actions, firstForestTurn,
            libraryCount, stressTail: observations.stressTail,
            before: state, nantuko: describe(hand[pairIndex]), forest: describe(hand[pairIndex + 1]) };
          return { before: state, nantuko: hand[pairIndex], forest: hand[pairIndex + 1] };
        }
      }
      if (!p || answered.get(seat) === p.id) continue;
      let answer, progress;
      if (state.phaseKey === 'PREGAME' && p.inputType === 'InputConfirm' && p.ok === 'Play' && p.cancel === 'Draw') {
        if (!p.okEnabled || !p.cancelEnabled) { waitForReady(seat, p); continue; }
        // Two players choose Play/Draw instead of a player-list prompt.
        answer = { action: seat === 1 ? 'ok' : 'cancel' };
        progress = () => { observations.startingChoice = { seat, answer, guestId: states[1].viewerId }; };
      } else if (p.playerChoices?.length) {
        // Let the guest start so the host draws on every own turn, including its first.
        const guestId = states[1].viewerId;
        expect(p.playerChoices).toContain(guestId);
        answer = { action: 'player', playerId: guestId };
      } else if (p.kind === 'choice') {
        if (p.context === 'playAbility') {
          expect(p.sourceCard?.name).toBe('Quirion Ranger');
          expect(p.choices).toHaveLength(1);
          answer = { choices: [p.choices[0].index] };
        } else answer = { choices: Array.from({ length: p.min }, (_, index) => index) };
      } else if (p.kind === 'reveal') answer = { action: 'ack' };
      else if (p.inputType === 'InputPassPriority') {
        if (!p.okEnabled) { waitForReady(seat, p); continue; }
        const ownMain = seat === 0 && state.activePlayerId === state.viewerId && state.phaseKey === 'MAIN1' && !state.stack.length;
        const forest = field.find(card => card.name === 'Forest');
        const ranger = field.find(card => card.name === 'Quirion Ranger');
        const rangerInHand = hand.find(card => card.name === 'Quirion Ranger');
        if (ownMain && firstForestTurn === undefined) {
          expect(ownTurns.size).toBe(1);
          const land = hand.find(card => card.name === 'Forest' && card.selectable);
          if (land) {
            answer = { action: 'card', key: land.key };
            progress = () => observations.forestPlays.push({ turn: state.turn, card: describe(land) });
          }
        } else if (ownMain && ownTurns.size >= 2 && state.turn !== firstForestTurn && forest && rangerInHand && mode !== 'casting-ranger') {
          expect(forest.tapped).toBe(false);
          if (rangerInHand.selectable) {
            answer = { action: 'card', key: rangerInHand.key };
            progress = () => { mode = 'casting-ranger'; };
          }
        } else if (ownMain && ownTurns.size >= 2 && state.turn !== firstForestTurn && forest && ranger
            && hand.some(card => card.name === 'Springheart Nantuko') && mode !== 'returning-forest'
            && (!observations.stressTail || libraryCount === 0)) {
          if (ranger.selectable) {
            answer = { action: 'card', key: ranger.key };
            progress = () => { mode = 'returning-forest'; };
          }
        } else if (ownMain && mode === 'returning-forest' && !forest) {
          // The strict final predicate above owns this post-return main phase.
          // Wait for its published hand/selectables rather than passing the turn.
          answer = null;
        } else {
          // Cancel can mean Undo; use only the engine's explicit End Turn offer.
          // A spell/ability on the stack needs ordinary passes so the postresolution
          // main-phase task is reachable even if its cards were drawn last.
          answer = { action: !state.stack.length && !(seat === 0 && state.turn === firstForestTurn)
            && (seat !== 0 || state.activePlayerId !== state.viewerId || ownTurns.has(state.turn))
            && p.cancelEnabled && p.cancel === 'End Turn' ? 'cancel' : 'ok' };
        }
      } else if (seat === 0 && mode === 'returning-forest' && p.inputType === 'InputSelectTargets') {
        const ranger = field.find(card => card.name === 'Quirion Ranger' && card.selectable && !card.highlighted);
        answer = ranger ? { action: 'card', key: ranger.key } : p.okEnabled ? { action: 'ok' } : null;
      } else if (seat === 0 && mode === 'returning-forest' && p.inputType === 'InputSelectCardsFromList') {
        const forest = field.find(card => card.name === 'Forest' && card.selectable && !card.highlighted);
        answer = forest ? { action: 'card', key: forest.key } : p.okEnabled ? { action: 'ok' } : null;
      } else if (p.okEnabled) answer = { action: 'ok' };
      else if (p.inputType === 'InputSelectCardsFromList') {
        // Keep the actual setup cards through genuine maximum-hand-size discards.
        const surplus = hand.find(card => card.selectable && !card.highlighted
          && (seat !== 0 || card.visualId !== retainedNantuko && card.name !== 'Quirion Ranger'));
        if (surplus) answer = { action: 'card', key: surplus.key };
      }
      if (!answer) {
        expect(['InputPassPriority', 'InputConfirmMulligan', 'InputSelectTargets', 'InputSelectCardsFromList'].includes(p.inputType)
          || p.inputType?.startsWith('InputPayMana'), JSON.stringify({ seat, mode, p, hand: hand.map(describe), field: field.map(describe) })).toBe(true);
        waitForReady(seat, p);
        continue;
      }
      waiting.delete(seat);
      const event = { elapsedMs: Date.now() - observations.startedAt, seat, turn: state.turn, phase: state.phaseKey,
        mode, libraryCount, sessionId: state.id, promptId: p.id, inputType: p.inputType, answer };
      observations.actions.push(event);
      fs.appendFileSync(observations.actionJournal, JSON.stringify({ phase: 'request', ...event }) + '\n');
      const applied = await submitMatchAction(clients[seat], state, answer);
      event.applied = applied;
      fs.appendFileSync(observations.actionJournal, JSON.stringify({ phase: 'receipt', ...event }) + '\n');
      if (applied) { answered.set(seat, p.id); actions++; progress?.(); }
    }
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  throw new Error(`Selection setup exhausted its bounded real-game budget: ${JSON.stringify({ actions, ownTurns: ownTurns.size, firstForestTurn, retainedNantuko, mode })}`);
}

test('Forest clicks cannot become Nantuko casts after a refresh; casting choices name the card and cancel', async () => {
  const startedAt = Date.now();
  const hostProfile = selectionProfile('card-selection');
  const { application, executable, dataPath } = await launchDesktop('card-selection', { dataPath: hostProfile.profile });
  const observations = { startedAt, stage: 'startup', forestPlays: [], actions: [],
    actionJournal: path.join(dataPath, 'card-selection-actions.jsonl'), lastStates: null,
    enginePreferences: [hostProfile], stressTail: process.env.MANA_SELECTION_STRESS_TAIL === '1' };
  let page, opponent;
  try {
    const guestProfile = selectionProfile('card-selection-opponent');
    observations.enginePreferences.push(guestProfile);
    opponent = startEngine(guestProfile.profile);
    page = await application.firstWindow();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await page.locator('#import-button').click();
    await page.locator('#import-name').fill('Nantuko regression');
    await page.locator('#import-text').fill('Deck\n55 Forest\n4 Springheart Nantuko\n1 Quirion Ranger');
    await page.locator('#preview-import').click();
    await page.locator('#confirm-import').click();
    await expect(page.locator('#deck-name')).toHaveValue('Nantuko regression');
    const saved = await page.evaluate(() => window.forge.request('snapshot'));
    await ready(opponent);
    const host = { request: (method, params = {}) => page.evaluate(({ method, params }) => window.forge.request(method, params), { method, params }) };
    observations.stage = 'real-game-setup';
    const { before, nantuko, forest } = await prepareSelectionTable(host, opponent, saved, observations, startedAt + 80000);
    await page.evaluate(state => window.openMatchState(state), before);
    await expect(page.locator('#match-prompt')).toHaveAttribute('data-prompt-id', before.prompt.id);
    observations.stage = 'forest-and-stale-handles';
    // Bring the chosen card above its overlapping neighbors through the hand's
    // normal keyboard focus behavior, then exercise the real pointer click.
    const forestButton = page.locator(`#match-hand [data-visual-card="${forest.visualId}"]`);
    await forestButton.focus();
    await forestButton.click();
    await expect(page.locator('#match-human .lands-row [aria-label="Forest"]')).toHaveCount(1);
    await expect.poll(async () => (await page.evaluate(() => window.forge.request('matchState'))).prompt?.inputType).toBe('InputPassPriority');
    const after = await page.evaluate(() => window.forge.request('matchState'));
    expect(after.turn).toBe(before.turn);
    expect(after.phaseKey).toBe('MAIN1');
    expect(after.activity.filter(entry => entry.kind === 'cast' && entry.cardName === 'Springheart Nantuko')).toEqual([]);
    // The host must reject this mismatch even if a client sends a newer prompt ID.
    await expect(page.evaluate(params => window.forge.request('matchAction', params), {
      sessionId: after.id, promptId: after.prompt.id, action: 'card', key: forest.key
    })).rejects.toThrow(/not visible/);
    expect((await page.evaluate(() => window.forge.request('matchState'))).prompt.id).toBe(after.prompt.id);

    // A card node retained from an older decision must not submit against a new one.
    const nantukoCard = page.locator(`#match-hand [data-visual-card="${nantuko.visualId}"]`);
    await nantukoCard.evaluate((element, stalePrompt) => {
      const currentPrompt = element.dataset.matchPrompt;
      element.dataset.matchPrompt = stalePrompt;
      element.click();
      element.dataset.matchPrompt = currentPrompt;
    }, before.prompt.id);
    await expect(page.locator('#toast')).toHaveText('The table updated. Select your card again.');
    expect((await page.evaluate(() => window.forge.request('matchState'))).prompt.id).toBe(after.prompt.id);

    await nantukoCard.focus();
    await nantukoCard.click();
    await expect(page.locator('#match-prompt h2')).toHaveText('Play Springheart Nantuko');
    observations.stage = 'casting-choice';
    await expect(page.locator('[data-ability-choice]')).toHaveCount(2);
    await expect(page.locator('[data-ability-choice="0"]')).toContainText('Cast as a creature');
    await expect(page.locator('[data-ability-choice="1"]')).toContainText('Bestow — cast as an Aura');
    const abilityChoice = await page.evaluate(() => window.forge.request('matchState'));
    // Even a syntactically valid empty selection cannot turn an auto-pass into
    // an implicit answer to an optional casting dialog.
    await expect(page.evaluate(params => window.forge.request('matchAction', params), {
      sessionId: abilityChoice.id, promptId: abilityChoice.prompt.id, action: 'passIfNoResponse', choices: []
    })).rejects.toThrow('needs your decision');
    expect((await page.evaluate(() => window.forge.request('matchState'))).prompt.id).toBe(abilityChoice.prompt.id);
    if (!executable) await page.screenshot({ path: test.info().outputPath('nantuko-choices.png') });
    await page.locator('#match-ability-cancel').click();
    observations.stage = 'cancel-choice';
    await expect(page.locator('#match-ok')).toBeVisible();
    const canceled = await page.evaluate(() => window.forge.request('matchState'));
    expect(canceled.phaseKey).toBe('MAIN1');
    expect(canceled.turn).toBe(before.turn);
    expect(canceled.activity.filter(entry => entry.kind === 'cast' && entry.cardName === 'Springheart Nantuko')).toEqual([]);
    await nantukoCard.focus();
    await nantukoCard.click();
    await page.locator('[data-ability-choice="0"]').click();
    observations.stage = 'cancel-mana-payment';
    await expect.poll(async () => (await page.evaluate(() => window.forge.request('matchState'))).prompt?.inputType).toMatch(/^InputPayMana/);
    await expect(page.locator('#match-cancel')).toBeEnabled();
    await page.locator('#match-cancel').click();
    await expect(page.locator('#match-prompt h2')).toHaveText('Play a card or continue.');
    expect((await page.evaluate(() => window.forge.request('snapshot'))).deck.revision).toBe(saved.deck.revision);
    expect(errors).toEqual([]);
    observations.stage = 'complete';
  } finally {
    observations.endedAt = Date.now();
    if (page && !page.isClosed()) {
      try { observations.final = await page.evaluate(() => window.forge.request('matchState')); }
      catch (error) { observations.finalReadError = error.message; }
    }
    try { fs.writeFileSync(path.join(dataPath, 'card-selection-fixture.json'), JSON.stringify(observations, null, 2)); }
    finally {
      try { await application.close(); }
      finally { if (opponent) await opponent.close(); }
    }
  }
});
