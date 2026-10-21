const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createLibraryDrawObserver } = require('./support/top-library.cjs');

const card = (visualId, name = 'Forest') => ({ visualId, name });
function state(topCard, hand, count, turn = 5) {
  return { viewerId: 1, turn, phaseKey: 'MAIN1', prompt: { inputType: 'InputPassPriority' }, players: [{ id: 1, zones: [
    { name: 'Library', count, topCard }, { name: 'Hand', cards: hand }
  ] }] };
}

test('a known top occurrence remains observable through an intervening top snapshot and arrives newly in hand', () => {
  const observer = createLibraryDrawObserver();
  observer.observe(state(card('top-a'), [card('opening-forest')], 40));
  observer.observe(state(card('top-b'), [card('opening-forest')], 39, 7));
  assert.equal(observer.drewTop, false);
  observer.observe(state(card('top-b'), [card('opening-forest'), card('top-a')], 39, 7));
  assert.equal(observer.drewTop, true);
  assert.deepEqual(Object.fromEntries(Object.entries(observer.snapshot().draw).filter(([key]) => key !== 'elapsedMs')), {
    visualId: 'top-a', name: 'Forest', observedTurn: 5, drawnTurn: 7, observedLibraryCount: 40, drawnLibraryCount: 39
  });
});

test('a same-named different occurrence, preexisting hand card or unchanged library cannot satisfy the draw oracle', () => {
  const observer = createLibraryDrawObserver();
  observer.observe(state(card('known-top'), [card('opening-forest')], 40));
  observer.observe(state(card('next-top'), [card('different-forest')], 39, 7));
  assert.equal(observer.drewTop, false, 'Names never substitute for the exact authorized opaque ID');
  observer.observe(state(card('next-top'), [card('known-top')], 40, 7));
  assert.equal(observer.drewTop, false, 'A library decrease is also required');
  const preexisting = createLibraryDrawObserver();
  preexisting.observe(state(card('already-in-hand'), [card('already-in-hand')], 40));
  preexisting.observe(state(null, [card('already-in-hand')], 39, 7));
  assert.equal(preexisting.drewTop, false, 'The occurrence must newly arrive in hand');
});

test('the last authorized library card can be drawn even when no visible top remains', () => {
  const observer = createLibraryDrawObserver();
  observer.observe(state(card('last-card'), [], 1));
  observer.observe(state(null, [card('last-card')], 0, 7));
  assert.equal(observer.drewTop, true);
});

test('late first-top setup reserves a bounded completion window without extending on polls or actions', () => {
  let clock = 0;
  const observer = createLibraryDrawObserver({ now: () => clock });
  assert.equal(observer.deadline, 100000);
  clock = 99000;
  observer.observe(state(card('late-top'), [], 10, 75));
  assert.equal(observer.deadline, 129000);
  clock = 115000;
  observer.observe(state(card('next-top'), [card('late-top')], 9, 77));
  observer.action(0, state(null, [], 9), { action: 'ok' }, true);
  assert.equal(observer.deadline, 129000);
  assert.equal(observer.snapshot().firstTopMs, 99000);
  assert.equal(observer.drewTop, true);
  clock = 130000;
  assert.ok(clock > observer.deadline, 'Unfinished encounters still reach a finite failure deadline');
});
