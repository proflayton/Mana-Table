// Bounded read-only JSON transport. Provenance validation belongs to each caller.
const TRANSIENT_HTTP = new Set([408, 429, 500, 502, 503, 504]);
const TRANSIENT_NETWORK = new Set(['ETIMEDOUT', 'ECONNRESET', 'EAI_AGAIN', 'ENETUNREACH',
  'UND_ERR_CONNECT_TIMEOUT', 'UND_ERR_HEADERS_TIMEOUT', 'UND_ERR_BODY_TIMEOUT', 'UND_ERR_SOCKET']);
function assert(value, message) { if (!value) throw new Error(message); }
function transientNetwork(error) {
  return error?.name === 'TimeoutError' || error?.name === 'AbortError'
    || TRANSIENT_NETWORK.has(error?.code) || TRANSIENT_NETWORK.has(error?.cause?.code);
}
function retryDelay(response, attempt, now) {
  const value = response?.headers?.get('retry-after');
  let delay = 1000 * 2 ** (attempt - 1);
  if (value !== null && value !== undefined) {
    const seconds = /^\d+$/.test(value) ? Number(value) : NaN;
    const until = Number.isFinite(seconds) ? now + seconds * 1000 : Date.parse(value);
    assert(Number.isFinite(until), 'Invalid GitHub Retry-After header; refusing to guess.');
    delay = Math.max(delay, until - now);
  } else if (response?.status === 429) delay = Math.max(delay, 60000);
  if (response?.headers?.get('x-ratelimit-remaining') === '0') {
    const reset = response.headers.get('x-ratelimit-reset');
    assert(/^\d+$/.test(reset || ''), 'Missing GitHub rate-limit reset; refusing to guess.');
    delay = Math.max(delay, Number(reset) * 1000 - now);
  }
  return delay;
}
function githubRead({ token, repo, userAgent, fetchImpl = fetch, now = Date.now,
  sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), signalTimeout = ms => AbortSignal.timeout(ms),
  report = snapshot => console.log(JSON.stringify(snapshot)) }) {
  assert(token, 'GH_TOKEN is required for read-only GitHub verification.');
  assert(typeof repo === 'string' && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)
    && repo.split('/').every(part => !/^\.+$/.test(part)), 'A valid GitHub repository is required.');
  return async (resource, { deadline } = {}) => {
    assert(typeof resource === 'string' && resource.startsWith('/repos/' + repo + '/')
      && !/[\r\n\\#]/.test(resource) && !resource.split('?')[0].split('/').some(part => part === '.' || part === '..'),
    'Unexpected read-only GitHub API repository or path.');
    assert(Number.isFinite(deadline), 'A shared GitHub lookup deadline is required.');
    for (let attempt = 1; attempt <= 3; attempt++) {
      const remaining = deadline - now();
      assert(remaining > 0, 'GitHub lookup deadline expired; verification remains blocked.');
      let response, failure;
      try {
        response = await fetchImpl('https://api.github.com' + resource, {
          method: 'GET',
          headers: { Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': userAgent, 'Cache-Control': 'no-cache' },
          redirect: 'error', signal: signalTimeout(Math.min(20000, Math.ceil(remaining))),
        });
        if (!response.ok) {
          // Never parse or retain remote error bodies (which may contain secrets).
          await response.body?.cancel();
          failure = new Error('Read-only GitHub request failed (HTTP ' + response.status + ').');
          failure.status = response.status;
          if (!TRANSIENT_HTTP.has(response.status)) throw failure;
        } else {
          const chunks = []; let bytes = 0;
          for await (const chunk of response.body) {
            bytes += chunk.length;
            assert(bytes <= 4 * 1024 * 1024, 'Read-only GitHub response exceeds its bound.');
            chunks.push(Buffer.from(chunk));
          }
          const result = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          assert(now() < deadline, 'GitHub lookup deadline expired; verification remains blocked.');
          return result;
        }
      } catch (error) {
        // JSON, size, permission and provenance failures never become retries.
        if (!transientNetwork(error)) throw error;
        failure = error;
      }
      if (attempt === 3) throw failure;
      const delay = retryDelay(response, attempt, now());
      assert(now() + delay < deadline, 'GitHub retry would exceed the lookup deadline; verification remains blocked.');
      report({ kind: 'github-read-retry', resource, attempt, delayMs: delay,
        reason: response ? 'HTTP ' + response.status : 'transient network failure' });
      await sleep(delay);
    }
  };
}
module.exports = { githubRead };
