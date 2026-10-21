const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');
const { testProfile, startEngine, ready } = require('./support/engine.cjs');

test('host shares a local invite, a guest joins by invite, and closing resets forwarding status', async ({}, testInfo) => {
  const { application, executable } = await launchDesktop('invite-host');
  const guest = startEngine(testProfile('invite-guest'));
  const previousClipboard = await application.evaluate(({ clipboard }) => clipboard.readText());
  try {
    const page = await application.firstWindow();
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await ready(guest);
    await page.locator('#multiplayer-tab').click();
    await page.locator('#multiplayer-format').selectOption('Commander');
    await page.locator('#multiplayer-player-count').selectOption('2');
    // Routine automated tests must not change the runner's router configuration.
    await page.locator('#multiplayer-forward').uncheck();
    await page.locator('#multiplayer-host').click();
    await expect(page.locator('#multiplayer-status')).toContainText('Direct connections only');
    await page.locator('#multiplayer-status summary').click();
    const copy = page.locator('[data-copy-invite]').filter({ hasText: 'Copy local invite' }).first();
    await expect(copy).toBeVisible();
    const invite = await copy.getAttribute('data-copy-invite');
    await copy.click();
    await expect(page.locator('#toast')).toContainText('Invite copied');
    expect(await application.evaluate(({ clipboard }) => clipboard.readText())).toBe(invite);
    const joined = await guest.request('multiplayerJoin', { address: invite });
    expect(joined.error).toBeNull();
    await expect(page.locator('#multiplayer-status li span').filter({ hasText: 'REMOTE' })).toHaveCount(1);
    const mask = [page.locator('.multiplayer-invite input'), page.locator('[data-copy-address]')];
    // Hidden packaged windows can stall Chromium's screenshot compositor.
    if (!executable) await page.screenshot({ path: testInfo.outputPath('lobby-wide.png'), fullPage: true, mask });
    await page.setViewportSize({ width: 1000, height: 740 });
    if (!executable) await page.screenshot({ path: testInfo.outputPath('lobby-compact.png'), fullPage: true, mask });
    // Reject typo before disconnecting the valid guest.
    await expect(guest.request('multiplayerJoin', { address: 'MT1-BROKEN' })).rejects.toThrow(/incomplete or mistyped/);
    expect((await guest.request('multiplayerState')).mode).toBe('joined');
    await guest.request('multiplayerClose');
    await page.locator('#multiplayer-leave').click();
    await expect(page.locator('#multiplayer-host')).toBeVisible();
    const closed = await page.evaluate(() => window.forge.request('multiplayerState'));
    expect(closed.portMapping).toBe('disabled');
    expect(closed.internetInvite).toBeNull();
    // Verify this client decodes pasted invites as well, with a fresh guest host.
    const other = await guest.request('multiplayerHost', { format: 'Constructed', playerCount: 2, autoPortForward: false });
    const otherInvite = other.addresses.find(address => address.invite)?.invite;
    expect(otherInvite).toBeTruthy();
    await page.locator('#multiplayer-address').fill(otherInvite);
    await page.locator('#multiplayer-join').click();
    await expect(page.locator('#multiplayer-leave')).toBeVisible();
    expect((await page.evaluate(() => window.forge.request('multiplayerState'))).mode).toBe('joined');
    await page.locator('#multiplayer-leave').click();
  } finally {
    await application.evaluate(({ clipboard }, value) => clipboard.writeText(value), previousClipboard);
    await guest.close();
    await application.close();
  }
});

test('three friends fill the fourth Commander seat with AI through the host lobby', async ({}, testInfo) => {
  const desktops = [];
  const third = startEngine(testProfile('ai-seat-third-friend'));
  try {
    await ready(third);
    for (const name of ['host', 'guest']) {
      const desktop = await launchDesktop('ai-seat-' + name);
      desktops.push(desktop);
      desktop.page = await desktop.application.firstWindow();
      await expect(desktop.page.locator('#loading')).toBeHidden({ timeout: 60000 });
      await desktop.page.locator('#import-button').click();
      await desktop.page.locator('#import-name').fill(name + ' Commander');
      await desktop.page.locator('#import-format').selectOption('Commander');
      await desktop.page.locator('#import-text').fill('Deck\n99 Forest\nCommander\n1 Rhys the Redeemed');
      await desktop.page.locator('#preview-import').click();
      await expect(desktop.page.locator('#confirm-import')).toBeEnabled();
      await desktop.page.locator('#confirm-import').click();
      await expect(desktop.page.locator('#deck-name')).toHaveValue(name + ' Commander');
      await desktop.page.locator('#multiplayer-tab').click();
    }
    const [host, guest] = desktops.map(desktop => desktop.page);
    const state = page => page.evaluate(() => window.forge.request('multiplayerState'));
    await host.locator('#multiplayer-format').selectOption('Commander');
    await host.locator('#multiplayer-player-count').selectOption('4');
    await host.locator('#multiplayer-forward').uncheck();
    await host.locator('#multiplayer-host').click();
    await expect(host.locator('[data-ai-add]')).toHaveCount(3);
    await expect(host.locator('[data-seat="0"] [data-ai-add]')).toHaveCount(0);

    const fourth = host.locator('[data-seat="3"]');
    await fourth.locator('[data-ai-add]').click();
    await expect(fourth).toHaveAttribute('data-seat-type', 'AI');
    await expect(fourth).toContainText('Ready');
    const initial = await state(host);
    expect(initial.aiOpponents.length).toBeGreaterThan(1);
    const changed = initial.aiOpponents.find(opponent => opponent.id !== initial.slots[3].opponentId);
    await fourth.locator('[data-ai-deck]').selectOption(changed.id);
    await expect.poll(async () => (await state(host)).slots[3].opponentId).toBe(changed.id);
    await fourth.locator('[data-ai-remove]').click();
    await expect(fourth).toHaveAttribute('data-seat-type', 'OPEN');
    await fourth.locator('[data-ai-add]').click();
    await expect(fourth).toHaveAttribute('data-seat-type', 'AI');

    const opened = await state(host);
    const address = opened.addresses.find(address => address.label === 'This computer')?.url || opened.addresses[0].url;
    await guest.locator('#multiplayer-address').fill(address);
    await guest.locator('#multiplayer-join').click();
    await expect(guest.locator('[data-seat="3"]')).toHaveAttribute('data-seat-type', 'AI');
    await expect(guest.locator('[data-ai-add], [data-ai-deck], [data-ai-remove]')).toHaveCount(0);
    const port = new URL('http://' + address).port;
    await third.request('multiplayerJoin', { address: '127.0.0.1:' + port });
    await expect(host.locator('[data-seat-type="REMOTE"]')).toHaveCount(2);
    await expect(host.locator('[data-seat="1"] [data-ai-add], [data-seat="2"] [data-ai-add]')).toHaveCount(0);
    await expect(guest.evaluate(({ tableId }) => window.forge.request('multiplayerSetSeat', { tableId, seat: 3, type: 'OPEN' }), opened)).rejects.toThrow(/host/i);

    const imported = await third.request('import', { name: 'Third friend Commander', format: 'Commander', text: 'Deck\n99 Forest\nCommander\n1 Rhys the Redeemed' });
    await third.request('multiplayerSelectDeck', { deckId: imported.id });
    await third.request('multiplayerReady', { ready: true });
    for (const page of [host, guest]) {
      await page.locator('#multiplayer-ready').click();
      await expect(page.locator('#multiplayer-ready')).toHaveAttribute('aria-pressed', 'true');
    }
    const readied = await state(host);
    const next = readied.aiOpponents.find(opponent => opponent.id !== readied.slots[3].opponentId);
    await fourth.locator('[data-ai-deck]').selectOption(next.id);
    await expect.poll(async () => (await state(host)).slots[3].opponentId).toBe(next.id);
    expect((await state(host)).slots.slice(0, 3).map(slot => slot.ready)).toEqual([true, true, true]);
    const configured = await state(host);
    expect(configured.slots.filter(slot => slot.type === 'LOCAL' || slot.type === 'REMOTE')).toHaveLength(3);
    await expect(guest.locator('[data-seat="3"]')).toContainText(configured.slots[3].deck);
    await expect(host.locator('#multiplayer-start')).toBeEnabled();
    await expect(guest.locator('#multiplayer-start')).toBeHidden();

    for (const size of [[1000, 740], [1540, 980]]) {
      await desktops[0].application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), size);
      await expect(fourth.locator('[data-ai-deck]')).toBeVisible();
      expect(await host.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (!desktops[0].executable) await host.screenshot({ path: testInfo.outputPath('ai-seat-host-' + size[0] + '.png'), fullPage: true,
        mask: [host.locator('.multiplayer-invite input'), host.locator('[data-copy-address]')] });
    }
    if (!desktops[1].executable) await guest.screenshot({ path: testInfo.outputPath('ai-seat-guest.png'), fullPage: true });
    await host.locator('#multiplayer-start').click();
    for (const page of [host, guest]) {
      await expect(page.locator('#match-view')).toBeVisible({ timeout: 30000 });
      await expect.poll(async () => {
        const match = await page.evaluate(() => window.forge.request('matchState'));
        return { total: match?.players?.length, ai: match?.players?.filter(player => player.name === configured.slots[3].name).length };
      }).toEqual({ total: 4, ai: 1 });
    }
  } finally {
    await third.close();
    for (const desktop of desktops.reverse()) await desktop.application.close();
  }
});

test('three friends choose three total seats and start Commander without AI', async ({}, testInfo) => {
  const { application, executable } = await launchDesktop('three-seat-host');
  const guests = [0, 1].map(index => startEngine(testProfile('three-seat-guest-' + index)));
  try {
    await Promise.all(guests.map(ready));
    const page = await application.firstWindow();
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await page.locator('#import-button').click();
    await page.locator('#import-name').fill('Three seat host');
    await page.locator('#import-format').selectOption('Commander');
    await page.locator('#import-text').fill('Commander\n1 Rhys the Redeemed\nDeck\n99 Forest');
    await page.locator('#preview-import').click();
    await expect(page.locator('#confirm-import')).toBeEnabled();
    await page.locator('#confirm-import').click();
    await expect(page.locator('#deck-name')).toHaveValue('Three seat host');
    await page.locator('#multiplayer-tab').click();
    await page.locator('#multiplayer-format').selectOption('Commander');
    await page.locator('#multiplayer-forward').uncheck();
    await page.locator('#multiplayer-host').click();
    const state = () => page.evaluate(() => window.forge.request('multiplayerState'));
    const opened = await state();
    expect(opened.playerCount).toBe(4);
    const address = opened.addresses.find(address => address.label === 'This computer').url;
    for (const [index, guest] of guests.entries()) {
      const deck = await guest.request('import', { name: 'Three seat guest ' + index, format: 'Commander', text: 'Commander\n1 Rhys the Redeemed\nDeck\n99 Forest' });
      await guest.request('multiplayerJoin', { address });
      await guest.request('multiplayerSelectDeck', { deckId: deck.id });
      await guest.request('multiplayerReady', { ready: true });
    }
    await page.locator('#multiplayer-ready').click();
    await expect(page.locator('#multiplayer-ready')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#multiplayer-start')).toBeDisabled();
    await expect(page.locator('#multiplayer-start-help')).toContainText('3/4');
    await expect(page.locator('#multiplayer-start-help')).toContainText('Total seats');
    await expect(page.locator('#multiplayer-seats-help')).toContainText('Choose 3 for three people');

    await page.getByLabel('Total seats').selectOption('3');
    await expect.poll(async () => (await state()).playerCount).toBe(3);
    const resized = await state();
    expect(resized.tableId).toBe(opened.tableId);
    expect(resized.slots).toHaveLength(3);
    expect(resized.slots.every(slot => slot.type !== 'AI' && slot.type !== 'OPEN' && slot.deck && !slot.ready)).toBe(true);
    await expect(page.locator('#multiplayer-ready')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('[data-seat]')).toHaveCount(3);
    await expect(page.locator('[data-ai-add], [data-ai-deck], [data-ai-remove]')).toHaveCount(0);
    for (const guest of guests) await guest.request('multiplayerReady', { ready: true });
    await page.locator('#multiplayer-ready').click();
    await expect(page.locator('#multiplayer-ready')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#multiplayer-start')).toBeEnabled();
    for (const size of [[1000, 740], [1540, 980]]) {
      await application.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), size);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (!executable) await page.screenshot({ path: testInfo.outputPath('three-human-seats-' + size[0] + '.png'), fullPage: true,
        mask: [page.locator('.multiplayer-invite input'), page.locator('[data-copy-address]')] });
    }
    await page.locator('#multiplayer-start').click();
    await expect(page.locator('#match-view')).toBeVisible({ timeout: 30000 });
    await expect.poll(async () => (await page.evaluate(() => window.forge.request('matchState')))?.players?.length).toBe(3);
    for (const guest of guests) {
      await expect.poll(async () => (await guest.request('matchState'))?.players?.length).toBe(3);
      const match = await guest.request('matchState');
      expect(match.players).toHaveLength(3);
      expect(match.players.some(player => /AI opponent/.test(player.name))).toBe(false);
    }
  } finally {
    await Promise.all(guests.map(guest => guest.close()));
    await application.close();
  }
});
