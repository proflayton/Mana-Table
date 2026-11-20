// Read-only publication prerequisite; never merge, rerun, cancel, or publish.
const fs = require('node:fs');
const { githubRead } = require('./github-read.cjs');
const REPO = 'proflayton/Mana-Table';
const COMPARE_PAGE_SIZE = 100, MAX_COMPARE_COMMITS = 10000;
const WORKFLOWS = [
  { file: 'test-build.yaml', jobs: ['Test with Java 17', 'Test with Java 21'], required: true },
  // Keep the established required context while it checks shared tooling only.
  // The MonoGame app is built and checked by the native release workflow.
  { file: 'mana-table.yml', jobs: ['desktop'], required: true },
  { file: 'ios-compat-gate.yml', jobs: ['iOS/MobiVM link audit'], required: false },
];
function assert(value, message) { if (!value) throw new Error(message); }
function validateSource(repo, sha) {
  assert(repo === REPO, 'Release verification is restricted to ' + REPO);
  assert(/^[a-f0-9]{40}$/.test(sha || ''), 'A full lowercase source SHA is required.');
}
function iosPath(file) {
  return /(^|\/)pom\.xml$/.test(file) || /\.java$/.test(file)
    || file.startsWith('forge-gui-ios/') || file === '.github/workflows/ios-compat-gate.yml';
}
function iosApplicability(comparison) {
  assert(comparison && Array.isArray(comparison.files), 'Missing source comparison files.');
  // GitHub compares expose at most 300 files. An incomplete diff cannot excuse a check.
  return comparison.files.length >= 300 || comparison.files.some(file => {
    assert(typeof file.filename === 'string', 'Invalid changed-file response.');
    return iosPath(file.filename) || typeof file.previous_filename === 'string' && iosPath(file.previous_filename);
  });
}
function validateComparison(comparison, before, sha) {
  assert(comparison.base_commit?.sha === before, 'Source comparison belongs to another base.');
  assert(comparison.status === 'ahead' || comparison.status === 'identical', 'Cannot prove iOS applicability from a diverged source range.');
  // Compare responses have base_commit and commits, not head_commit. Require a
  // complete commit list whose terminal SHA is our source; truncated histories
  // cannot establish this contract and must leave publication blocked.
  assert(Array.isArray(comparison.commits) && Number.isInteger(comparison.total_commits)
    && comparison.total_commits === comparison.commits.length, 'Source comparison commit history is incomplete.');
  assert(comparison.status === 'identical' ? before === sha && comparison.total_commits === 0
    : comparison.total_commits > 0 && comparison.commits.at(-1)?.sha === sha, 'Source comparison belongs to another head.');
}
async function readComparison({ api, repo, before, sha, deadline, now = Date.now }) {
  validateSource(repo, sha);
  assert(/^[a-f0-9]{40}$/.test(before || '') && !/^0{40}$/.test(before), 'A full lowercase nonzero comparison base SHA is required.');
  assert(Number.isFinite(deadline), 'A shared comparison deadline is required.');
  const commits = [], seen = new Set();
  let first, metadata, pages = 1;
  for (let page = 1; page <= pages; page++) {
    assert(now() < deadline, 'Source comparison deadline expired; publication remains blocked.');
    const response = await api('/repos/' + REPO + '/compare/' + before + '...' + sha
      + '?per_page=' + COMPARE_PAGE_SIZE + '&page=' + page, { deadline });
    assert(now() < deadline, 'Source comparison deadline expired; publication remains blocked.');
    assert(response?.base_commit?.sha === before, 'Source comparison belongs to another base.');
    assert(response.merge_base_commit?.sha === before, 'Source comparison belongs to another merge base.');
    assert(response.status === 'ahead' || response.status === 'identical', 'Cannot prove iOS applicability from a diverged source range.');
    assert(Number.isSafeInteger(response.total_commits) && response.total_commits >= 0
      && response.total_commits <= MAX_COMPARE_COMMITS, 'Source comparison exceeds its bounded commit history.');
    assert(response.ahead_by === response.total_commits && response.behind_by === 0,
      'Source comparison has inconsistent ahead/behind metadata.');
    const current = { base: response.base_commit.sha, mergeBase: response.merge_base_commit.sha,
      status: response.status, total: response.total_commits, ahead: response.ahead_by, behind: response.behind_by };
    if (page === 1) {
      metadata = current;
      pages = Math.max(1, Math.ceil(metadata.total / COMPARE_PAGE_SIZE));
      assert(Array.isArray(response.files) && response.files.length <= 300, 'Missing or oversized source comparison files.');
      for (const file of response.files)
        assert(file && typeof file.filename === 'string' && file.filename.length > 0
          && (file.previous_filename === undefined || typeof file.previous_filename === 'string' && file.previous_filename.length > 0),
        'Invalid changed-file response.');
      // Only page one carries the whole comparison's capped changed-file list.
      first = { base_commit: response.base_commit, status: response.status, total_commits: metadata.total, files: response.files };
    } else {
      assert(Object.keys(metadata).every(key => current[key] === metadata[key]), 'Source comparison metadata changed between pages.');
      assert(response.files === undefined || response.files === null || Array.isArray(response.files) && response.files.length === 0,
        'Later source comparison page unexpectedly includes changed files.');
    }
    const expected = Math.min(COMPARE_PAGE_SIZE, metadata.total - commits.length);
    assert(Array.isArray(response.commits) && response.commits.length === expected,
      'Source comparison page commit history is incomplete or oversized.');
    for (const commit of response.commits) {
      assert(/^[a-f0-9]{40}$/.test(commit?.sha || '') && commit.sha !== before,
        'Source comparison contains an invalid commit identity.');
      assert(!seen.has(commit.sha), 'Source comparison contains duplicate commit identities.');
      seen.add(commit.sha);
      // Preserve GitHub's page order. Merge histories need no invented linear
      // parent or timestamp ordering, and full commit payloads need not survive.
      commits.push({ sha: commit.sha });
    }
  }
  const comparison = { ...first, commits };
  validateComparison(comparison, before, sha);
  return comparison;
}
function assessRuns(spec, response, context) {
  assert(response && Array.isArray(response.workflow_runs) && Number.isInteger(response.total_count), 'Invalid workflow-run response.');
  assert(response.total_count < 100 && response.total_count === response.workflow_runs.length, 'Incomplete workflow-run response; refusing partial readiness.');
  const runs = response.workflow_runs;
  for (const run of runs) {
    assert(run.head_sha === context.sha && run.head_branch === 'master' && run.event === 'push'
      && run.repository?.full_name === REPO && run.path?.split('@')[0] === '.github/workflows/' + spec.file
      && Number.isSafeInteger(run.id) && Number.isSafeInteger(run.run_attempt), 'Wrong source, repository, event, or workflow in check response.');
  }
  if (!runs.length) return { state: spec.required || context.iosRequired ? 'pending' : 'optional', reason: 'No matching push run yet.', runs };
  // Preserve failures from every observed current-source run. A rerun of the same
  // run is represented by its latest attempt and may repair that run normally.
  for (const run of runs) if (run.status === 'completed')
    assert(run.conclusion === 'success', spec.file + ' failed: ' + run.conclusion + ' (' + run.html_url + ')');
  return { state: runs.every(run => run.status === 'completed') ? 'jobs' : 'pending', runs };
}
function assessJobs(spec, run, response, sha) {
  assert(response && Array.isArray(response.jobs) && response.total_count === response.jobs.length && response.total_count < 100, 'Incomplete jobs response.');
  if (!response.jobs.length) {
    assert(run.status !== 'completed', 'Successful workflow has no job evidence: ' + spec.file);
    return 'pending';
  }
  for (const job of response.jobs) {
    assert(job.run_id === run.id && job.head_sha === sha
      && (job.run_attempt === undefined || Number.isInteger(job.run_attempt) && job.run_attempt > 0 && job.run_attempt <= run.run_attempt), 'Job evidence belongs to another source or attempt.');
    if (job.status === 'completed') assert(job.conclusion === 'success', spec.file + ' job failed or skipped: ' + job.name);
  }
  for (const name of spec.jobs) if (!response.jobs.some(job => job.name === name)) {
    assert(run.status !== 'completed', 'Missing required job: ' + name);
    return 'pending';
  }
  return run.status === 'completed' && response.jobs.every(job => job.status === 'completed') ? 'passed' : 'pending';
}
async function inspect({ api, repo, sha, iosRequired }) {
  validateSource(repo, sha);
  const master = await api('/repos/' + REPO + '/git/ref/heads/master');
  assert(master.object?.sha === sha, 'Master advanced or source does not match; publication is blocked.');
  const evidence = [];
  for (const spec of WORKFLOWS) {
    const query = '?event=push&branch=master&head_sha=' + sha + '&per_page=100';
    const response = await api('/repos/' + REPO + '/actions/workflows/' + spec.file + '/runs' + query);
    const assessed = assessRuns(spec, response, { sha, iosRequired });
    let state = assessed.state;
    if (assessed.runs.length) {
      state = 'passed';
      for (const run of assessed.runs) {
        if (run.status === 'queued' || run.status === 'requested' || run.status === 'waiting') { state = 'pending'; continue; }
        // GitHub's latest view retains successful jobs when only failed jobs are
        // rerun. Do not require every matrix job to execute again in one attempt.
        const jobs = await api('/repos/' + REPO + '/actions/runs/' + run.id + '/jobs?filter=latest&per_page=100');
        if (assessJobs(spec, run, jobs, sha) !== 'passed') state = 'pending';
      }
    }
    evidence.push({ workflow: spec.file, state, runs: assessed.runs.map(run => ({ id: run.id, attempt: run.run_attempt, status: run.status, conclusion: run.conclusion, url: run.html_url })) });
  }
  if (evidence.every(item => item.state === 'passed' || item.state === 'optional')) {
    // Recheck current attempts only when otherwise ready, so a rerun started
    // during job inspection cannot reuse a previous attempt's success.
    for (const item of evidence) for (const run of item.runs) {
      const latest = await api('/repos/' + REPO + '/actions/runs/' + run.id);
      assert(latest.id === run.id, 'Current run response belongs to another workflow run.');
      const spec = WORKFLOWS.find(candidate => candidate.file === item.workflow);
      const fresh = assessRuns(spec, { total_count: 1, workflow_runs: [latest] }, { sha, iosRequired });
      if (latest.run_attempt !== run.attempt || fresh.state !== 'jobs') item.state = 'pending';
    }
  }
  // The native publication workflow is deliberately absent: it is this caller.
  return { ready: evidence.every(item => item.state === 'passed' || item.state === 'optional'), evidence };
}
async function waitForCI({ api, repo, sha, iosRequired, timeoutMs = 4200000, pollMs = 30000,
  now = Date.now, sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), report = () => {} }) {
  validateSource(repo, sha);
  assert(Number.isFinite(timeoutMs) && timeoutMs > 0 && timeoutMs <= 4200000, 'CI wait must be bounded to at most 70 minutes.');
  assert(Number.isFinite(pollMs) && pollMs >= 1 && pollMs <= 30000, 'CI polling must be between 1 and 30000 milliseconds.');
  const deadline = now() + timeoutMs;
  const boundedApi = resource => api(resource, { deadline });
  while (now() < deadline) {
    const result = await inspect({ api: boundedApi, repo, sha, iosRequired });
    report(result);
    if (result.ready) {
      const finalMaster = await boundedApi('/repos/' + REPO + '/git/ref/heads/master');
      assert(finalMaster.object?.sha === sha, 'Master advanced during verification; publication is blocked.');
      return result;
    }
    await sleep(Math.min(pollMs, Math.max(0, deadline - now())));
  }
  throw new Error('Timed out waiting for all required exact-source CI runs; publication remains blocked.');
}
function githubApi(token) {
  assert(token, 'GH_TOKEN is required for read-only Actions verification.');
  return githubRead({ token, repo: REPO, userAgent: 'ManaTable-release-ci-gate' });
}
async function main(env = process.env) {
  const repo = env.GITHUB_REPOSITORY, sha = env.GITHUB_SHA;
  validateSource(repo, sha);
  assert(env.GITHUB_REF === 'refs/heads/master', 'Publication is restricted to master.');
  const api = githubApi(env.GH_TOKEN);
  const deadline = Date.now() + 4200000;
  let iosRequired = false;
  if (env.GITHUB_EVENT_NAME === 'push') {
    const event = JSON.parse(fs.readFileSync(env.GITHUB_EVENT_PATH, 'utf8'));
    assert(event.repository?.full_name === REPO && event.ref === 'refs/heads/master' && event.after === sha
      && /^[a-f0-9]{40}$/.test(event.before || '') && !/^0{40}$/.test(event.before), 'Cannot establish the exact master push change range.');
    const comparison = await readComparison({ api, repo, before: event.before, sha, deadline });
    iosRequired = iosApplicability(comparison);
  } else {
    assert(env.GITHUB_EVENT_NAME === 'workflow_dispatch', 'Unsupported publication event.');
    // Dispatch has no original multi-commit push range. Require iOS rather than
    // excuse it using an incomplete guess about the last commit's changed paths.
    iosRequired = true;
  }
  const result = await waitForCI({ api, repo, sha, iosRequired, timeoutMs: deadline - Date.now(),
    report: snapshot => console.log(JSON.stringify({ source: sha, iosRequired, ...snapshot })) });
  console.log('All required exact-source CI passed; existing publication guards may proceed.');
  return result;
}
module.exports = { REPO, WORKFLOWS, validateSource, validateComparison, readComparison, iosPath, iosApplicability, assessRuns, assessJobs, inspect, waitForCI, main };
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
