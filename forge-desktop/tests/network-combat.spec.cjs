const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const { launchDesktop } = require('./support/desktop.cjs');
const { startEngine, testProfile, ready } = require('./support/engine.cjs');
const { startTable, playCombat, submit } = require('./support/network-combat.cjs');

test('a three-player table declares split attacks and blocks directly on the battlefield', async () => {
  test.setTimeout(240000);
  const desktops = [], clients = [];
  let observer;
  const captures = new Set();
  try {
    for (const name of ['attacker', 'blocker']) {
      const desktop = await launchDesktop(`network-combat-${name}`);
      desktops.push(desktop);
      await desktop.application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].webContents.beginFrameSubscription(() => {}));
      const page = await desktop.application.firstWindow();
      await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
      await page.locator('#multiplayer-tab').click();
      clients.push({ page, desktop, request: (method, params = {}) => page.evaluate(({ method, params }) => window.forge.request(method, params), { method, params }) });
    }
    observer = startEngine(testProfile('combat-observer'));
    await ready(observer); clients.push(observer);
    await startTable(clients);
    await playCombat(clients, 0, async interaction => {
      const { client, state, stage, answer, attackerId, blockerId, defenderId } = interaction;
      if (!client.page) return submit(client, state, answer);
      const { page, desktop } = client;
      await expect(page.locator('#match-view')).toBeVisible();
      await expect(page.locator('#match-prompt')).toHaveAttribute('data-prompt-id', state.prompt.id);
      await expect(page.locator('#combat-view')).toBeHidden();
      const controls = page.getByRole('region', { name: 'Battlefield combat' });
      await expect(controls).toBeVisible();
      await expect(page.locator('#table-combat-confirm')).toBeInViewport();
      if (stage === 'attack' || stage === 'block') {
        const source = page.locator(`#match-human .battlefield-card[data-table-combat="${stage === 'attack' ? attackerId : blockerId}"]`);
        const target = stage === 'attack' ? page.locator(`.match-arena .match-life[data-match-player="${defenderId}"]`)
          : page.locator(`.match-arena .battlefield-card[data-table-combat="${attackerId}"]`);
        await source.click();
        await expect(source).toHaveClass(/table-combat-selected/);
        await expect(target).toHaveClass(/combat-target-ready/);
        expect((await client.request('matchState')).prompt.id).toBe(state.prompt.id);
        if (!captures.has(stage)) {
          await expect(controls).toContainText(stage === 'attack' ? 'Choose a glowing player' : 'Choose a glowing attacker');
          const png = await desktop.application.evaluate(async ({ BrowserWindow }) =>
            (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'));
          fs.writeFileSync(test.info().outputPath(`battlefield-${stage}.png`), Buffer.from(png, 'base64'));
          captures.add(stage);
          await page.keyboard.press('Escape');
          await expect(source).not.toHaveClass(/table-combat-selected/);
          expect((await client.request('matchState')).prompt.id).toBe(state.prompt.id);
          await source.click();
        }
        const removing = state.combat.attackers.some(attack => stage === 'attack'
          ? attack.cardId === attackerId && attack.defender?.id === defenderId
          : attack.cardId === attackerId && attack.blockerIds.includes(blockerId));
        if (removing) await controls.getByRole('button', { name: stage === 'attack' ? 'Recall attacker' : 'Remove block', exact: true }).click();
        else await target.click();
      } else {
        await expect(page.locator('.table-combat-lines > path')).not.toHaveCount(0);
        await expect(page.locator('.combat-under-attack')).toHaveCount(2);
        if (stage === 'confirmBlock') {
          await expect(page.locator('#match-human .table-blocker')).not.toHaveCount(0);
          await expect(page.locator('.table-combat-lines > .block-line')).not.toHaveCount(0);
        }
        for (const [width, height] of [[1000, 740], [1540, 980]]) {
          await desktop.application.evaluate(({ BrowserWindow }, [width, height]) => BrowserWindow.getAllWindows()[0].setSize(width, height), [width, height]);
          await expect(page.locator('#table-combat-confirm')).toBeInViewport();
        }
        await page.locator('#table-combat-confirm').click();
      }
    });
    expect([...captures].sort()).toEqual(['attack', 'block']);
  } finally {
    await Promise.all(desktops.map(desktop => desktop.application.close()));
    if (observer) await observer.close();
  }
});
