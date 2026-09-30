const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');
const landPlay = require('../encounters/land-play.cjs');

test('match scrolling survives unchanged polls, selection updates, and unrelated board changes', async () => {
  const { application } = await launchDesktop('match-scroll');
  try {
    const page = await application.firstWindow();
    const { before } = await landPlay.prepare(page, 'Commander');
    // Display-only snapshots exercise the public renderer entry point. Keep the
    // actual engine paused; no fixture card or choice is sent as a game action.
    const fixture = structuredClone(before);
    fixture.revision += 100000;
    fixture.prompt = { id: 'scroll-fixture', kind: 'choice', min: 0, max: 2,
      message: 'Choose a card from this list.', choices: Array.from({ length: 45 }, (_, index) => ({ index, label: `Choice ${index + 1}` })) };
    const human = fixture.players.find(player => player.human), graveyard = human.zones.find(zone => zone.name === 'Graveyard');
    const card = human.zones.find(zone => zone.name === 'Hand').cards[0];
    graveyard.cards = Array.from({ length: 40 }, (_, i) => ({ ...card, key: '', visualId: `scroll-card:${i}`, selectable: false }));
    graveyard.count = graveyard.cards.length;
    const show = () => page.evaluate(state => window.openMatchState(state), fixture);
    await show();
    const choices = page.locator('#match-choices');
    const pane = page.locator('.match-prompt-body');
    await pane.evaluate(element => { element.scrollTop = 400; });
    const choiceScroll = await pane.evaluate(element => element.scrollTop);
    expect(choiceScroll).toBeGreaterThan(0);
    for (let i = 0; i < 3; i++) { fixture.revision++; await show(); }
    expect(await pane.evaluate(element => element.scrollTop)).toBe(choiceScroll);
    // Selecting a visible option redraws its selected state, not the scroll location.
    await choices.locator('[data-choice="12"]').evaluate(element => element.click());
    expect(await pane.evaluate(element => element.scrollTop)).toBe(choiceScroll);
    fixture.prompt.message += ' You may revise your selection.';
    fixture.revision++; await show();
    expect(await pane.evaluate(element => element.scrollTop)).toBe(choiceScroll);
    const drawer = page.locator('#match-human .match-zone[data-zone$="-Graveyard"]');
    await drawer.locator('summary').click();
    const content = drawer.locator(':scope > div');
    await content.evaluate(element => { element.scrollTop = 250; });
    const drawerScroll = await content.evaluate(element => element.scrollTop);
    expect(drawerScroll).toBeGreaterThan(0);
    human.life--;
    fixture.revision++; await show();
    await expect(drawer).toHaveAttribute('open');
    expect(await content.evaluate(element => element.scrollTop)).toBe(drawerScroll);
    fixture.prompt.id = 'next-scroll-fixture';
    fixture.revision++; await show();
    expect(await pane.evaluate(element => element.scrollTop)).toBe(0);
  } finally { await application.close(); }
});
