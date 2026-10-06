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
