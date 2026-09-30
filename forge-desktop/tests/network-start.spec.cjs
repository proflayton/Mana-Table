const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');

test('two desktop clients ready their decks, mulligan into turn one, then reopen cleanly', async () => {
  const clients = [];
  try {
    for (const seat of ['host', 'guest']) {
      const client = await launchDesktop(`network-start-${seat}`);
      clients.push(client);
      client.page = await client.application.firstWindow();
      await expect(client.page.locator('#loading')).toBeHidden({ timeout: 60000 });
      await client.page.locator('#import-button').click();
      await client.page.locator('#import-name').fill(`${seat} Commander`);
      await client.page.locator('#import-text').fill('Deck\n99 Forest\nCommander\n1 Rhys the Redeemed');
      await client.page.locator('#preview-import').click();
      await expect(client.page.locator('#confirm-import')).toBeEnabled();
      await client.page.locator('#confirm-import').click();
      await expect(client.page.locator('#deck-name')).toHaveValue(`${seat} Commander`);
      await client.page.locator('#multiplayer-tab').click();
    }
    const [host, guest] = clients.map(client => client.page);
    await host.locator('#multiplayer-format').selectOption('Commander');
    await host.locator('#multiplayer-player-count').selectOption('2');
    await host.locator('#multiplayer-forward').uncheck();
    await host.locator('#multiplayer-host').click();
    await expect(host.locator('#multiplayer-loadout')).toBeVisible();
    await expect(host.locator('#multiplayer-start')).toBeDisabled();
    await expect(host.locator('#multiplayer-start-help')).toContainText('1/2');
    const lobby = await host.evaluate(() => window.forge.request('multiplayerState'));
    const invite = lobby.addresses.find(address => address.invite)?.invite;
    expect(invite).toBeTruthy();
    await guest.locator('#multiplayer-address').fill(invite);
    await guest.locator('#multiplayer-join').click();
    await expect(guest.locator('#multiplayer-loadout')).toBeVisible();
    await expect(host.locator('#multiplayer-start-help')).toContainText('choose a deck');
    for (const page of [host, guest]) {
      await expect(page.locator('#multiplayer-format')).toHaveValue('Commander');
      await page.locator('#multiplayer-ready').click();
      await expect(page.locator('#multiplayer-ready')).toHaveAttribute('aria-pressed', 'true');
      await expect(page.locator('#multiplayer-status li.local')).toContainText('Commander');
    }
    await expect.poll(async () => {
      const state = await host.evaluate(() => window.forge.request('multiplayerState'));
      return state.slots.every(slot => slot.ready && slot.deck);
    }).toBe(true);
    await host.locator('#multiplayer-start').click();
    for (const page of [host, guest]) {
      await expect(page.locator('#match-view')).toBeVisible({ timeout: 30000 });
      await expect.poll(async () => (await page.evaluate(() => window.forge.request('matchState')))?.players?.length).toBe(2);
    }
    let mulliganed = false, selectedBottomCard = false, started = false;
    const previous = new Map();
    const checkedStablePrompt = new Set();
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline && !started) {
      for (const page of [host, guest]) {
        const state = await page.evaluate(() => window.forge.request('matchState'));
        expect(state.status, state.error).not.toBe('error');
        if (state.turn > 0) { started = true; break; }
        const prompt = state.prompt;
        if (!prompt || previous.get(page) === prompt.id
          || await page.locator('#match-prompt').getAttribute('data-prompt-id') !== prompt.id) continue;
        if (prompt.okEnabled && !checkedStablePrompt.has(page)) {
          await expect(page.locator('#match-ok')).toBeEnabled();
          await expect(page.locator('#match-ok')).toHaveText(prompt.ok);
          await page.locator('#match-ok').focus();
          const button = await page.locator('#match-ok').elementHandle();
          // More than two multiplayer polls must leave the same control focused.
          await page.waitForTimeout(800);
          expect(await button.evaluate(element => ({ connected: element.isConnected,
            focused: document.activeElement === element, label: element.textContent,
            currentLabel: document.getElementById('match-ok')?.textContent }))).toEqual({
            connected: true, focused: true, label: prompt.ok, currentLabel: prompt.ok });
          await button.dispose();
          checkedStablePrompt.add(page);
        }
        if (prompt.inputType === 'InputConfirmMulligan' && prompt.okEnabled && prompt.cancelEnabled) {
          if (page === guest && !mulliganed) {
            await page.locator('#match-cancel').click();
            mulliganed = true;
          } else await page.locator('#match-ok').click();
        } else if (prompt.inputType === 'InputLondonMulligan' && prompt.cancelEnabled) {
          const hand = state.players.find(player => player.id === state.viewerId).zones.find(zone => zone.name === 'Hand').cards;
          expect(hand).toHaveLength(7);
          await expect(page.locator('#match-hand .actionable')).toHaveCount(7);
          await expect(page.locator('#match-ok')).toBeDisabled();
          const card = page.locator(`#match-hand [data-match-card="${hand[0].key}"]`);
          await card.focus();
          await card.click();
          await expect(card).toHaveClass(/chosen/);
          await expect(page.locator('#match-ok')).toBeEnabled();
          await page.locator('#match-ok').click();
          selectedBottomCard = true;
        } else if (prompt.kind === 'choice' && prompt.min === 1 && prompt.max === 1) {
          await page.locator('[data-choice="0"]').click();
        } else if (prompt.okEnabled) await page.locator('#match-ok').click();
        else continue;
        previous.set(page, prompt.id);
      }
      await host.waitForTimeout(75);
    }
    expect(mulliganed).toBe(true);
    expect(selectedBottomCard).toBe(true);
    expect(started, 'The table must advance after the remote player finishes their mulligan').toBe(true);
    const dataPath = clients[0].dataPath;
    for (const client of [...clients].reverse()) await client.application.close();
    clients.length = 0;
    const reopened = await launchDesktop('network-reopen', { dataPath, preferences: null });
    clients.push(reopened);
    const page = await reopened.application.firstWindow();
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await expect(page.locator('#deck-name')).toHaveValue('host Commander');
    const reset = await page.evaluate(() => window.forge.request('multiplayerState'));
    expect(reset.mode).toBe('idle');
    expect(reset.hosting).toBe(false);
  } finally {
    for (const client of clients.reverse()) await client.application.close();
  }
});
