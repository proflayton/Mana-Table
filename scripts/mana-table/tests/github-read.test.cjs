const test = require('node:test');
const assert = require('node:assert/strict');
const { Readable } = require('node:stream');
const { githubRead } = require('../github-read.cjs');
const resource = '/repos/proflayton/Mana-Table/actions/runs/10';
function response(status = 200, data = '{"id":10}', headers = {}) {
  return { status, ok: status === 200, headers: new Headers(headers),
    body: status === 200 ? Readable.from([Buffer.from(data)]) : { cancel: async () => {} } };
}
function fixture(results) {
  let clock = 0;
  const calls = [], sleeps = [], timeouts = [], reports = [];
  const now = () => clock;
  const sleep = async ms => { sleeps.push(ms); clock += ms; };
  const api = githubRead({ token: 'test-only', repo: 'proflayton/Mana-Table', userAgent: 'test', now, sleep,
    signalTimeout: ms => { timeouts.push(ms); return undefined; }, report: item => reports.push(item),
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      const result = results.shift();
      assert.ok(result, 'Unexpected extra HTTP attempt.');
      if (result instanceof Error) throw result;
      if (typeof result === 'function') return result(ms => { clock += ms; });
      return result;
    } });
  return { api, now, sleep, calls, sleeps, timeouts, reports };
}
test('a timed-out read retries the same GET, with no mutation or token in diagnostics', async () => {
  const f = fixture([Object.assign(new Error('secret upstream error'), { name: 'TimeoutError' }), response()]);
  assert.deepEqual(await f.api(resource, { deadline: 480000 }), { id: 10 });
  assert.deepEqual(f.sleeps, [1000]);
  assert.equal(f.calls.length, 2);
  assert.ok(f.calls.every(call => call.url === 'https://api.github.com' + resource
    && call.options.method === 'GET' && call.options.redirect === 'error' && !call.options.body));
  assert.doesNotMatch(JSON.stringify(f.reports), /secret|test-only/);
});
test('transient HTTP statuses have at most three attempts with bounded exponential backoff', async () => {
  for (const status of [408, 500, 502, 503, 504]) {
    const f = fixture([response(status), response(status), response(status)]);
    await assert.rejects(f.api(resource, { deadline: 480000 }), new RegExp('HTTP ' + status));
    assert.equal(f.calls.length, 3);
    assert.deepEqual(f.sleeps, [1000, 2000]);
  }
});
test('known transient socket failures retry but unknown network errors fail closed', async () => {
  for (const code of ['ECONNRESET', 'ETIMEDOUT', 'EAI_AGAIN', 'UND_ERR_SOCKET']) {
    const f = fixture([new TypeError('fetch failed', { cause: { code } }), response()]);
    assert.equal((await f.api(resource, { deadline: 480000 })).id, 10);
  }
  const f = fixture([new TypeError('fetch failed', { cause: { code: 'ENOTFOUND' } }), response()]);
  await assert.rejects(f.api(resource, { deadline: 480000 }), /fetch failed/);
  assert.equal(f.calls.length, 1);
});
test('rate limits respect Retry-After and reset, or wait a minute without guidance', async () => {
  for (const [headers, delay] of [[{ 'retry-after': '7' }, 7000],
    [{ 'retry-after': 'Thu, 01 Jan 1970 00:00:09 GMT' }, 9000],
    [{ 'retry-after': '1', 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': '11' }, 11000], [{}, 60000]]) {
    const f = fixture([response(429, '', headers), response()]);
    await f.api(resource, { deadline: 480000 });
    assert.deepEqual(f.sleeps, [delay]);
  }
  for (const headers of [{ 'retry-after': 'unknown' }, { 'x-ratelimit-remaining': '0' }]) {
    const f = fixture([response(429, '', headers), response()]);
    await assert.rejects(f.api(resource, { deadline: 480000 }), /refusing to guess/);
    assert.equal(f.calls.length, 1);
  }
});
test('requests and retries cannot extend the caller deadline', async () => {
  const f = fixture([advance => { advance(19000); throw Object.assign(new Error('timeout'), { name: 'AbortError' }); }, response()]);
  await assert.rejects(f.api(resource, { deadline: 19500 }), /deadline/);
  assert.deepEqual(f.timeouts, [19500]);
  assert.deepEqual(f.sleeps, []);
  const limited = fixture([response(503), response()]);
  await limited.api(resource, { deadline: 1500 });
  assert.deepEqual(limited.timeouts, [1500, 500]);
  const rateLimited = fixture([response(429), response()]);
  await assert.rejects(rateLimited.api(resource, { deadline: 50000 }), /deadline/);
  assert.deepEqual(rateLimited.sleeps, []);
  const late = fixture([advance => { advance(100); return response(); }]);
  await assert.rejects(late.api(resource, { deadline: 100 }), /deadline/);
});
test('malformed, oversized, unauthorized, missing and redirected responses never retry', async () => {
  for (const result of [response(200, '{'), response(200, 'x'.repeat(4 * 1024 * 1024 + 1)),
    ...[301, 401, 403, 404, 422, 501].map(status => response(status))]) {
    const f = fixture([result, response()]);
    await assert.rejects(f.api(resource, { deadline: 480000 }));
    assert.equal(f.calls.length, 1);
    assert.deepEqual(f.sleeps, []);
  }
});
test('another repository, path traversal and missing deadlines fail before HTTP', async () => {
  for (const invalid of ['/repos/else/repo/actions', resource + '#fragment', '/repos/proflayton/Mana-Table/../else',
    '/repos/proflayton/Mana-Table/\\else', resource + '\n']) {
    const f = fixture([response()]);
    await assert.rejects(f.api(invalid, { deadline: 480000 }), /repository or path/);
    assert.equal(f.calls.length, 0);
  }
  const f = fixture([response()]);
  await assert.rejects(f.api(resource), /deadline/);
  assert.equal(f.calls.length, 0);
});
