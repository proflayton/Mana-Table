const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');
const path = require('node:path');
const fs = require('node:fs');

test('match table plays cards through engine prompts and resumes after deck browsing', async () => {
  const { application, appPath, executable, dataPath } = await launchDesktop('match');
  const errors = [];
  const observations = { openingHands: [], retainedHand: null, priority: [], final: null };
  const describe = card => ({ name: card.name, type: card.type, manaCost: card.manaCost,
    visualId: card.visualId, selectable: card.selectable, highlighted: card.highlighted });
  const land = card => card.type.includes('Land');
  const oneDrop = card => card.type.includes('Creature') && String(card.manaCost).replace(/[{}\s]/g, '') === 'R';
  try {
    const page = await application.firstWindow();
    page.on('pageerror', error => errors.push(error.message));
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await page.locator('#import-button').click();
    await page.locator('#import-name').fill('Feedback check');
    await page.locator('#import-text').fill('Deck\n24 Mountain\n4 Raging Goblin\n4 Goblin Arsonist\n4 Monastery Swiftspear\n4 Ghitu Lavarunner\n4 Foundry Street Denizen\n4 Mogg Fanatic\n4 Goblin Cohort\n4 Jackal Pup\n4 Akroan Crusader');
    await page.locator('#preview-import').click();
    await page.locator('#confirm-import').click();
    await expect(page.locator('#deck-name')).toHaveValue('Feedback check');
    await page.locator('#play-match').click();
    await expect(page.locator('#match-deck-label')).toHaveText('Feedback check');
    await expect(page.locator('#match-opponent-choice option')).toHaveCount(2);
    await page.locator('#match-start').click();
    await expect(page.locator('#match-view')).toBeVisible();
    await expect(page.locator('#match-motion')).toHaveText('Animations off');
    await page.locator('#match-motion').click();
    await expect(page.locator('#match-motion')).toHaveAttribute('aria-pressed', 'true');
    expect(await page.evaluate(() => localStorage.getItem('mana-table-motion'))).toBe('on');
    let played = false, paid = false, mulligan = false, yourTurn = false, opponentTurn = false, oldPrompt, mulliganCount = 0;
    const observedPriority = new Set();
    const deadline = Date.now() + 65000;
    while (Date.now() < deadline) {
      const state = await page.evaluate(() => window.forge.request('matchState'));
      expect(state.status, state.error).not.toBe('error');
      const p = state.prompt;
      const human = state.players.find(player => player.human);
      const hand = human?.zones.find(zone => zone.name === 'Hand').cards || [];
      const field = human?.zones.find(zone => zone.name === 'Battlefield').cards || [];
      const renderedPrompt = await page.locator('#match-prompt').getAttribute('data-prompt-id');
      if (p?.inputType === 'InputPassPriority' && !observedPriority.has(p.id)) {
        observedPriority.add(p.id);
        observations.priority.push({ id: p.id, renderedPrompt, turn: state.turn, phase: state.phaseKey,
          activePlayerId: state.activePlayerId, canAutoPass: p.canAutoPass, mana: human.mana,
          hand: hand.map(describe), field: field.map(describe) });
        if (observations.priority.length > 60) observations.priority.shift();
      }
      // The engine can replace a transient prompt before the renderer's next poll.
      // Act only when both sides agree, rather than waiting for an obsolete ID.
      if (!p || p.id === oldPrompt || renderedPrompt !== p.id) {
        await page.waitForTimeout(100); continue;
      }
      if (state.turn > 0) {
        const active = state.players.find(player => player.id === state.activePlayerId);
        await expect(page.locator('#match-turn-owner')).toHaveText(active.human ? 'Your turn' : `${active.name}’s turn`);
        await expect(page.locator('#match-turn')).toHaveText(`Turn ${state.turn}`);
        const optional = p.inputType === 'InputPassPriority' && (!active.human || !['MAIN1', 'MAIN2'].includes(state.phaseKey) || state.stack?.length);
        await expect(page.locator('#match-prompt .eyebrow')).toHaveText(optional
          ? `${active.human ? 'YOUR TURN' : 'OPPONENT’S TURN'} · OPTIONAL RESPONSE`
          : active.human ? 'YOUR ACTION' : 'OPPONENT’S TURN · YOUR CHOICE');
        await expect(page.locator('#match-phase [aria-current="step"]')).toHaveCount(1);
        await expect(page.locator('#match-phase-name')).not.toBeEmpty();
        if (active.human) yourTurn = true; else opponentTurn = true;
      }
      if (played && paid && yourTurn && opponentTurn && field.some(card => card.type.includes('Creature'))) break;
      oldPrompt = p.id;
      if (p.kind === 'choice') {
        for (let i = 0; i < p.min; i++) await page.locator(`[data-choice="${i}"]`).click();
        if (p.min !== 1 || p.max !== 1) await page.locator('#match-submit').click();
      } else if (p.kind === 'reveal') await page.locator('#match-submit').click();
      else if (p.inputType === 'InputConfirmMulligan') {
        // Exercise a redraw, then keep a hand that can actually complete this
        // cast/payment scenario. A shuffled zero-land hand is not a UI failure.
        const playable = hand.some(land) && hand.some(oneDrop);
        observations.openingHands.push({ id: p.id, mulliganCount, playable, hand: hand.map(describe) });
        if (!mulligan || !playable) {
          expect(mulliganCount, JSON.stringify(observations.openingHands)).toBeLessThan(3);
          expect(p.cancelEnabled).toBe(true);
          mulligan = true; mulliganCount++;
          await page.locator('#match-cancel').click();
        } else await page.locator('#match-ok').click();
      } else if (p.inputType === 'InputLondonMulligan') {
        const retained = hand.filter(card => !card.highlighted);
        const keepLand = retained.find(land), keepCreature = retained.find(oneDrop);
        expect(keepLand, JSON.stringify(retained.map(describe))).toBeTruthy();
        expect(keepCreature, JSON.stringify(retained.map(describe))).toBeTruthy();
        if (p.okEnabled) {
          observations.retainedHand = retained.map(describe);
          await page.locator('#match-ok').click();
        } else {
          const surplus = retained.find(card => card.selectable && card !== keepLand && card !== keepCreature);
          expect(surplus, JSON.stringify(retained.map(describe))).toBeTruthy();
          const button = page.locator(`[data-match-card="${surplus.key}"]`);
          await button.focus(); await button.click();
        }
      } else if (p.inputType === 'InputPassPriority') {
        // Once a creature has resolved, pass to exercise an opponent turn instead
        // of repeatedly trying to pay for another spell with spent mana.
        const card = field.some(card => card.type.includes('Creature')) ? null
          : hand.find(card => card.selectable && card.type.includes('Land')) || hand.find(card => card.selectable && card.type.includes('Creature'));
        if (card) {
          const button = page.locator(`[data-match-card="${card.key}"]`);
          await button.focus(); await button.click(); played = true;
        }
        else await page.locator('#match-ok').click();
      } else if (p.okEnabled) {
        if (p.inputType.startsWith('InputPayMana')) paid = true;
        await page.locator('#match-ok').click();
      } else {
        const card = human.zones.flatMap(zone => zone.cards).find(card => card.selectable && !card.highlighted);
        expect(card, JSON.stringify(p)).toBeTruthy();
        const button = page.locator(`[data-match-card="${card.key}"]`);
        await button.focus(); await button.click();
      }
    }
    const diagnostics = await page.evaluate(async () => {
      const state = await window.forge.request('matchState');
      const human = state.players.find(player => player.human);
      return { status: state.status, turn: state.turn, phase: state.phaseKey, prompt: state.prompt,
        hand: human?.zones.find(zone => zone.name === 'Hand').cards, mana: human?.mana,
        field: human?.zones.find(zone => zone.name === 'Battlefield').cards,
        events: state.activity?.slice(-12), toast: document.getElementById('toast').textContent };
    });
    observations.final = diagnostics;
    const failure = JSON.stringify({ ...observations, played, paid, mulligan, yourTurn, opponentTurn });
    expect(errors).toEqual([]);
    expect(played, failure).toBe(true);
    expect(paid, failure).toBe(true);
    expect(mulligan).toBe(true);
    expect(yourTurn).toBe(true);
    expect(opponentTurn, failure).toBe(true);
    await expect(page.locator('.match-arena')).toHaveClass(/scene-active/);
    expect(Number(await page.locator('.table-scene-canvas').getAttribute('data-frames'))).toBeGreaterThan(0);
    await expect(page.locator('#match-human .battlefield-card[data-scene-card]')).not.toHaveCount(0);
    await expect(page.locator('#match-history-list')).toContainText('You cast');
    await expect(page.locator('#match-history-list')).toContainText('played');
    const history = await page.locator('#match-history-list').textContent();
    const eventIds = await page.locator('#match-history-list li').evaluateAll(items => items.map(item => item.dataset.eventId));
    expect(new Set(eventIds).size).toBe(eventIds.length);
    await page.waitForTimeout(500); // Repeated polling must retain history without duplicates.
    await expect(page.locator('#match-history-list')).toHaveText(history);
    await page.locator('#match-motion').click();
    await expect(page.locator('#match-motion')).toHaveText('Animations off');
    expect(await page.locator('#match-view').evaluate(element => element.getAnimations({ subtree: true }).length)).toBe(0);
    await expect(page.locator('#match-human .match-card')).not.toHaveCount(0);
    // These bounds belong to the flat fallback. The 3D scene deliberately hides
    // those DOM rows; its canvas/card bindings were checked above.
    await page.locator('#match-renderer').click();
    await expect(page.locator('#match-renderer')).toHaveText('2D table');
    await expect(page.locator('.match-arena')).not.toHaveClass(/scene-active/);
    for (const size of [[1540, 980], [1120, 740], [1000, 740]]) {
      await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), size);
      const layout = await page.locator('#match-human').evaluate(element => {
        const bounds = selector => element.querySelector(selector).getBoundingClientRect().toJSON();
        return { lands: bounds('.lands-row'), permanents: bounds('.permanents-row'),
          cards: [...element.querySelectorAll('.battlefield-row .match-card')].map(card => ({
            card: card.getBoundingClientRect().toJSON(), row: card.parentElement.getBoundingClientRect().toJSON()
          })) };
      });
      // Compact artwork tiles preserve front/back ranks even in short windows.
      expect(layout.lands.y).toBeGreaterThan(layout.permanents.y);
      for (const { card, row } of layout.cards) {
        expect(card.y).toBeGreaterThanOrEqual(row.y - 4); // Hover lifts a card slightly.
        expect(card.y + card.height).toBeLessThanOrEqual(row.y + row.height + 1);
      }
    }
    await expect(page.locator('#match-human .lands-row .match-card')).not.toHaveCount(0);
    await expect(page.locator('#match-human .permanents-row .match-card')).not.toHaveCount(0);
    await page.locator('#match-human .match-zone summary').first().click();
    await expect(page.locator('#match-human .match-zone[open] .zone-drawer-title')).toContainText('Graveyard');
    await page.locator('#match-human .match-zone summary').last().click();
    await expect(page.locator('.match-zone[open]')).toHaveCount(1);
    await page.locator('#match-human .match-zone summary').last().click();
    await page.locator('#match-renderer').click();
    await expect(page.locator('.match-arena')).toHaveClass(/scene-active/);
    if (!executable) await page.screenshot({ path: path.join(appPath, 'test-results/match-table.png'), fullPage: true });
    await page.locator('#match-back').click();
    await expect(page.locator('#deck-name')).toHaveValue('Feedback check');
    await expect(page.locator('#main-count')).toHaveText('60');
    await page.locator('#match-tab').click();
    await expect(page.locator('#match-setup')).not.toBeVisible();
    await expect(page.locator('#match-view')).toBeVisible();
    await expect(page.locator('#match-history-list')).toHaveText(history);
    await expect(page.locator('#match-motion')).toHaveAttribute('aria-pressed', 'false');
    await page.locator('#match-concede').click();
    await page.locator('#match-concede-confirm').click();
    await expect(page.locator('#match-prompt')).toContainText('Defeat');
    await page.locator('#match-again').click();
    await expect(page.locator('#match-setup')).toBeVisible();
    expect(errors).toEqual([]);
  } finally {
    fs.writeFileSync(path.join(dataPath, 'match-fixture.json'), JSON.stringify({ ...observations, errors }, null, 2));
    await application.close();
  }
});
