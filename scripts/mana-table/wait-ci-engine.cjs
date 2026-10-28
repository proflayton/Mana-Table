// Read-only artifact discovery. Full Java checks remain separate required gates.
const fs = require('node:fs');
const { githubRead } = require('./github-read.cjs');
const WORKFLOW = '.github/workflows/test-build.yaml';
function assert(value, message) { if (!value) throw new Error(message); }
function validateContext({ repo, checkoutSha, headSha, event }) {
  assert(typeof repo === 'string' && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)
    && repo.split('/').every(part => !/^\.+$/.test(part)), 'A valid owner/repository context is required.');
  for (const sha of [checkoutSha, headSha]) assert(/^[a-f0-9]{40}$/.test(sha || ''), 'Full lowercase checkout and head SHAs are required.');
  assert(['push', 'pull_request'].includes(event), 'Shared engine discovery requires a push or pull_request producer.');
  assert(event !== 'push' || checkoutSha === headSha, 'A push producer must match the exact checkout SHA.');
}
function validateRun(run, context) {
  assert(run && Number.isSafeInteger(run.id) && run.id > 0 && Number.isSafeInteger(run.run_attempt) && run.run_attempt > 0
    && Number.isSafeInteger(run.run_number) && run.run_number > 0
    && run.repository?.full_name === context.repo && Number.isSafeInteger(run.repository.id)
    && run.head_sha === context.headSha && run.event === context.event && run.path?.split('@')[0] === WORKFLOW
    && typeof run.head_branch === 'string' && (context.event !== 'push' || run.head_branch === 'master'),
  'Engine producer belongs to another source, repository, event, or workflow.');
}
function completePage(response, key) {
  assert(response && Array.isArray(response[key]) && Number.isInteger(response.total_count)
    && response.total_count === response[key].length && response.total_count < 100,
  'Incomplete engine ' + key + ' response; refusing partial evidence.');
  return response[key];
}
async function inspect({ api, ...context }) {
  validateContext(context);
  const response = await api('/repos/' + context.repo + '/actions/workflows/test-build.yaml/runs?event=' + context.event
    + '&head_sha=' + context.headSha + '&per_page=100');
  const runs = completePage(response, 'workflow_runs');
  for (const run of runs) validateRun(run, context);
  if (!runs.length) return { ready: false, reason: 'Waiting for the exact-source Java producer.' };
  const run = [...runs].sort((a, b) => b.run_number - a.run_number || b.id - a.id)[0];
  const artifactResponse = await api('/repos/' + context.repo + '/actions/runs/' + run.id + '/artifacts?per_page=100');
  const artifacts = completePage(artifactResponse, 'artifacts');
  const matching = artifacts.filter(artifact => artifact.name === 'engine-' + context.checkoutSha && artifact.expired === false);
  assert(matching.length <= 1, 'Ambiguous source-bound engine artifacts.');
  if (!matching.length) {
    assert(run.status !== 'completed', 'Engine producer completed without the exact-checkout artifact. Rebuild the producer for this source; expired or older-source artifacts cannot be used.');
    return { ready: false, runId: run.id, reason: 'Waiting for the exact-checkout engine upload.' };
  }
  const artifact = matching[0];
  assert(Number.isSafeInteger(artifact.id) && artifact.id > 0 && artifact.size_in_bytes > 0
    && artifact.workflow_run?.id === run.id && artifact.workflow_run.head_sha === context.headSha
    && artifact.workflow_run.repository_id === run.repository.id && artifact.workflow_run.head_branch === run.head_branch,
  'Engine artifact belongs to another producer run or source.');
  const latest = await api('/repos/' + context.repo + '/actions/runs/' + run.id);
  validateRun(latest, context);
  assert(latest.id === run.id, 'Engine producer detail belongs to another run.');
  if (latest.run_attempt !== run.run_attempt) return { ready: false, runId: run.id, reason: 'Producer attempt changed during artifact discovery.' };
  const attempt = await api('/repos/' + context.repo + '/actions/runs/' + run.id + '/attempts/' + run.run_attempt);
  validateRun(attempt, context);
  assert(attempt.id === run.id && attempt.run_attempt === run.run_attempt, 'Engine producer attempt belongs to another run.');
  const started = Date.parse(attempt.run_started_at), uploaded = Date.parse(artifact.created_at);
  assert(Number.isFinite(started) && Number.isFinite(uploaded), 'Missing engine producer attempt or artifact timestamps.');
  if (uploaded < started) {
    assert(latest.status !== 'completed', 'Current producer attempt completed without a fresh engine upload. Rebuild this exact source.');
    return { ready: false, runId: run.id, reason: 'Waiting for the current producer attempt to replace its previous upload.' };
  }
  // An upload may be available while Java tests still run, or before they fail.
  // This is source/integrity evidence, never permission to merge or publish.
  return { ready: true, runId: run.id, artifactId: artifact.id, sourceRevision: context.checkoutSha, sourceHeadSha: context.headSha };
}
async function waitForEngine({ api, timeoutMs = 480000, pollMs = 5000, now = Date.now,
  sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), report = () => {}, ...context }) {
  validateContext(context);
  assert(Number.isFinite(timeoutMs) && timeoutMs > 0 && timeoutMs <= 480000, 'Engine wait must be bounded to eight minutes.');
  assert(Number.isFinite(pollMs) && pollMs > 0 && pollMs <= 5000, 'Engine polling must be bounded to five seconds.');
  const deadline = now() + timeoutMs;
  const boundedApi = resource => api(resource, { deadline });
  while (now() < deadline) {
    const result = await inspect({ api: boundedApi, ...context });
    report(result);
    if (result.ready) return result;
    await sleep(Math.min(pollMs, Math.max(0, deadline - now())));
  }
  throw new Error('Timed out waiting for the exact-checkout engine artifact. Rebuild test-build.yaml for this source; no older source can substitute.');
}
function githubApi(token, repo) {
  assert(token, 'GH_TOKEN with actions:read is required for shared engine discovery.');
  return githubRead({ token, repo, userAgent: 'ManaTable-engine-artifact' });
}
function writeOutput(file, result) {
  assert(file && result.ready === true && Number.isSafeInteger(result.runId) && result.runId > 0, 'Only a proven engine run may be exported.');
  fs.appendFileSync(file, 'run-id=' + result.runId + '\n');
}
async function main(env = process.env) {
  const context = { repo: env.GITHUB_REPOSITORY, checkoutSha: env.GITHUB_SHA, headSha: env.SOURCE_HEAD_SHA, event: env.SOURCE_EVENT };
  validateContext(context);
  assert(env.GITHUB_OUTPUT, 'GITHUB_OUTPUT is required for the shared engine run ID.');
  let previous;
  const result = await waitForEngine({ ...context, api: githubApi(env.GH_TOKEN, context.repo), report: snapshot => {
    const state = JSON.stringify(snapshot);
    if (state !== previous) console.log(state);
    previous = state;
  } });
  writeOutput(env.GITHUB_OUTPUT, result);
  return result;
}
module.exports = { validateContext, validateRun, inspect, waitForEngine, writeOutput, main };
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
