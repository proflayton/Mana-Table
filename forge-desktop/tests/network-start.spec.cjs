const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');

test('two desktop clients ready their selected decks, start a Commander match, then reopen cleanly', async () => {
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
