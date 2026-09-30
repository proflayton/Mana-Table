const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');
const { localDeck, playChorus, zone, waitFor } = require('./support/top-library.cjs');

test('Elven Chorus shows an inspectable top card on the 3D and 2D deck pile, then hides it when removed', async () => {
  test.setTimeout(180000);
  const { application } = await launchDesktop('top-library');
  try {
    const page = await application.firstWindow(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    const client = { request: (command, params) => page.evaluate(({ command, params }) => window.forge.request(command, params), { command, params }) };
    await client.request('import', { name: 'Elven Chorus encounter', format: 'Constructed', text: localDeck });
    let state, prepared = false;
    for (let attempt = 0; attempt < 40 && !prepared; attempt++) {
      await client.request('matchStart', { opponent: 'green' });
      state = await waitFor(async () => {
        const next = await client.request('matchState');
        if (next.prompt?.inputType?.includes('Mulligan')) return next;
        if (next.prompt?.okEnabled) await client.request('matchAction', { sessionId: next.id, promptId: next.prompt.id, action: 'ok' });
      }, 'No opening hand');
      const hand = zone(state, 'Hand').cards;
      prepared = ['Elven Chorus', 'Naturalize'].every(name => hand.some(card => card.name === name));
      if (!prepared) await client.request('matchConcede', { sessionId: state.id });
    }
    expect(prepared, 'A legal opening hand with Chorus and removal').toBe(true);
    await page.evaluate(state => window.openMatchState(state), state);
    await expect(page.locator('#match-view')).toBeVisible();
    await expect(page.locator('.library-top-card')).toHaveCount(0);
    let inspected = false;
    await playChorus([client], { inspect: async state => {
      if (inspected || !zone(state, 'Library').topCard.type.includes('Land') || !state.prompt || state.stack.length) return;
      const top = zone(state, 'Library').topCard;
      const card = page.locator('#match-human .library-top-card');
      await expect(card).toHaveAttribute('data-visual-card', top.visualId);
      await expect(card).toHaveAccessibleName(`Top of library: ${top.name}`);
      await expect(page.locator('#match-opponent .library-top-card')).toHaveCount(0);
      await expect(page.locator('.match-arena')).toHaveClass(/scene-active/);
      for (const mode of ['3D', '2D']) {
        if (mode === '2D') await page.locator('#match-renderer').click();
        await card.hover();
        await expect(page.locator('#card-zoom')).toBeVisible();
        await expect(page.locator('#card-zoom')).toHaveAccessibleName(top.name);
        const promptId = (await client.request('matchState')).prompt.id;
        await card.click();
        expect((await client.request('matchState')).prompt.id).toBe(promptId);
        await expect(card).not.toHaveClass(/actionable/);
      }
      await page.locator('#match-renderer').click();
      await page.keyboard.press('Tab');
      await card.focus();
      await expect(page.locator('#card-zoom')).toBeVisible();
      await page.screenshot({ path: test.info().outputPath('elven-chorus-top-card.png') });
      inspected = true;
    } });
    expect(inspected).toBe(true);
    await expect(page.locator('.library-top-card')).toHaveCount(0);
    await expect(page.locator('#match-human .library-back')).toHaveCount(1);
    await expect(page.locator('#card-zoom')).toBeHidden();
    expect(errors).toEqual([]);
  } finally { await application.close(); }
});
