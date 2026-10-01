const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const { EngineClient } = require('../../engine-client.cjs');
const { engineOptions } = require('../../runtime.cjs');
const project = path.resolve(__dirname, '../../..');

function testProfile(prefix) {
  const directory = path.join(project, 'forge-desktop', 'test-results');
  fs.mkdirSync(directory, { recursive: true });
  return fs.mkdtempSync(path.join(directory, `${prefix}-`));
}

function startEngine(userData) {
  return new EngineClient(engineOptions({ project, userData }));
}

async function ready(engine) {
  const signal = AbortSignal.timeout(60000);
  while (engine.status.state !== 'ready') {
    if (engine.status.state === 'error') throw new Error(engine.status.message);
    await once(engine, 'status', { signal });
  }
}

// Input display tasks can publish another prompt between a poll and its answer.
// A stale rejection has not applied the action: let the driver choose again from
// the new state. Never replay card keys or choices against a different prompt.
async function submitMatchAction(engine, state, answer) {
  try {
    await engine.request('matchAction', { sessionId: state.id, promptId: state.prompt.id, ...answer });
    return true;
  } catch (error) {
    if (error.message !== 'That choice has changed. Use the current prompt.') throw error;
    const current = await engine.request('matchState');
    if (current.id !== state.id || current.prompt?.id === state.prompt.id) throw error;
    return false;
  }
}

module.exports = { testProfile, startEngine, ready, submitMatchAction };
