const { test } = require('node:test');
const assert = require('node:assert/strict');
const { submitMatchAction } = require('./support/engine.cjs');

const stale = 'That choice has changed. Use the current prompt.';
const state = Object.freeze({ id: 'table-one', prompt: Object.freeze({ id: 'old-prompt' }) });
const answer = Object.freeze({ action: 'card', key: 'old-card-key' });
const command = { sessionId: state.id, promptId: state.prompt.id, ...answer };

function rejectedEngine(error, current = { id: state.id, prompt: { id: 'new-prompt' } }) {
  const calls = [];
  return {
    calls,
    async request(method, params) {
      calls.push({ method, params });
      if (method === 'matchAction') throw error;
      assert.equal(method, 'matchState');
      return current;
    }
  };
}

test('accepted match input is sent once with its original session and prompt', async () => {
  const calls = [];
  const engine = { async request(method, params) { calls.push({ method, params }); } };
  assert.equal(await submitMatchAction(engine, state, answer), true);
  assert.deepEqual(calls, [{ method: 'matchAction', params: command }]);
});

for (const [transport, message] of [
  ['engine', stale],
  ['Electron IPC', `Error invoking remote method 'engine': Error: ${stale}`],
  ['Playwright', `page.evaluate: Error: ${stale}`],
  ['Playwright over Electron IPC', `page.evaluate: Error: Error invoking remote method 'engine': Error: ${stale}`]
]) test(`a proven stale prompt from ${transport} requires a fresh decision without replaying input`, async () => {
  const engine = rejectedEngine(new Error(message));
  assert.equal(await submitMatchAction(engine, state, answer), false);
  assert.deepEqual(engine.calls, [
    { method: 'matchAction', params: command },
    { method: 'matchState', params: undefined }
  ]);
});

test('unknown errors, wrong IPC methods and extra error details remain failures without polling or replay', async () => {
  for (const message of [
    'The engine connection closed.',
    `Unexpected failure: ${stale}`,
    `Error invoking remote method 'save': Error: ${stale}`,
    `page.evaluate: Error: Error invoking remote method 'engine': Error: ${stale} Extra failure details.`
  ]) {
    const original = new Error(message);
    const engine = rejectedEngine(original);
    await assert.rejects(submitMatchAction(engine, state, answer), error => error === original);
    assert.deepEqual(engine.calls, [{ method: 'matchAction', params: command }]);
  }
});

test('a wrapped stale rejection still fails unless the same session has advanced past the submitted prompt', async () => {
  for (const current of [
    { id: state.id, prompt: { id: state.prompt.id } },
    { id: 'different-table', prompt: { id: 'new-prompt' } },
    { prompt: { id: 'new-prompt' } },
    null
  ]) {
    const original = new Error(`page.evaluate: Error: Error invoking remote method 'engine': Error: ${stale}`);
    const engine = rejectedEngine(original, current);
    await assert.rejects(submitMatchAction(engine, state, answer), error => error === original);
    assert.deepEqual(engine.calls, [
      { method: 'matchAction', params: command },
      { method: 'matchState', params: undefined }
    ]);
  }
});

test('failure to obtain fresh state remains a failure and never replays the action', async () => {
  const stateError = new Error('The engine stopped before it could return state.');
  const calls = [];
  const engine = {
    async request(method, params) {
      calls.push({ method, params });
      throw method === 'matchAction' ? new Error(stale) : stateError;
    }
  };
  await assert.rejects(submitMatchAction(engine, state, answer), error => error === stateError);
  assert.deepEqual(calls, [
    { method: 'matchAction', params: command },
    { method: 'matchState', params: undefined }
  ]);
});
