const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

// The HTTP server remains on this event loop while real Bash/curl/JDK children
// run asynchronously. No downloaded tools, Maven build or external HTTP is used.
const helper = path.resolve(process.env.IOS_JAR_DOWNLOAD_TEST_HELPER || path.join(__dirname, 'download-jar.sh'));
const bash = process.env.BASH || (process.platform === 'win32' ? 'C:/Program Files/Git/bin/bash.exe' : 'bash');
const environment = { ...process.env, NO_PROXY: '127.0.0.1,localhost', no_proxy: '127.0.0.1,localhost' };
const shellPath = value => process.platform === 'win32'
  ? value.replace(/\\/g, '/').replace(/^([A-Za-z]):/, (_, drive) => '/' + drive.toLowerCase()) : value;
let directory, server, origin, validJar;
const routes = new Map(), receipts = [];

async function shell(script, args, overrides = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(bash, ['--noprofile', '--norc', '-c', script, 'jar-download-test', ...args], {
      env: { ...environment, ...overrides }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
    });
    let stdout = '', stderr = '', timedOut = false;
    const retain = (previous, chunk) => (previous + chunk).slice(-32768);
    child.stdout.on('data', chunk => { stdout = retain(stdout, chunk); });
    child.stderr.on('data', chunk => { stderr = retain(stderr, chunk); });
    const timeout = setTimeout(() => { timedOut = true; child.kill(); }, 30000);
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.on('close', (code, signal) => {
      clearTimeout(timeout);
      if (timedOut) reject(new Error('Local JAR fixture child exceeded 30 seconds.'));
      else resolve({ code, signal, stdout, stderr });
    });
  });
}

test.before(async () => {
  directory = process.env.IOS_JAR_DOWNLOAD_TEST_DIRECTORY
    ? path.resolve(process.env.IOS_JAR_DOWNLOAD_TEST_DIRECTORY) : fs.mkdtempSync(path.join(os.tmpdir(), 'ios-jar-download-'));
  if (process.env.IOS_JAR_DOWNLOAD_TEST_DIRECTORY) fs.mkdirSync(directory); // exclusive evidence directory
  const payload = path.join(directory, 'real jar input');
  fs.mkdirSync(payload); fs.writeFileSync(path.join(payload, 'payload.txt'), 'maintained local archive fixture\n');
  const fixture = path.join(directory, 'valid.jar');
  const made = await shell('set -e; command -v curl; command -v jar; jar cf "$1" -C "$2" payload.txt', [shellPath(fixture), shellPath(payload)]);
  assert.equal(made.code, 0, 'Real curl and JDK jar tools must be available: ' + made.stderr);
  validJar = fs.readFileSync(fixture);
  server = http.createServer((request, response) => {
    const route = routes.get(request.url);
    if (!route) { response.writeHead(404); response.end(); return; }
    route.requests++;
    if (route.onRequest) route.onRequest(route.requests);
    const kind = route.replies[Math.min(route.requests - 1, route.replies.length - 1)];
    if (kind === '503') { response.writeHead(503); response.end('<html>temporary unavailable</html>'); }
    else if (kind === 'html') { response.writeHead(200); response.end('<html>not an archive</html>'); }
    else if (kind === 'truncated') { response.writeHead(200); response.end(validJar.subarray(0, Math.floor(validJar.length / 2))); }
    else { response.writeHead(200); response.end(validJar); }
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  origin = 'http://127.0.0.1:' + server.address().port;
});

test.after(async () => {
  if (server) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
  if (directory) {
    fs.writeFileSync(path.join(directory, 'results.json'), JSON.stringify({ helper, bash, cases: receipts }, null, 2) + '\n');
    if (!process.env.IOS_JAR_DOWNLOAD_TEST_DIRECTORY) {
      const resolvedDirectory = fs.realpathSync(directory), resolvedTemporary = fs.realpathSync(os.tmpdir());
      assert.equal(path.dirname(resolvedDirectory), resolvedTemporary, 'Only this direct temporary child may be removed.');
      assert.ok(path.basename(resolvedDirectory).startsWith('ios-jar-download-'));
      fs.rmSync(resolvedDirectory, { recursive: true, force: true });
    }
  }
});

async function fixture(name, replies, options = {}) {
  const folder = path.join(directory, name + ' cache with spaces'); fs.mkdirSync(folder);
  const destination = path.join(folder, 'tool.jar');
  if (options.existing) fs.writeFileSync(destination, options.existing);
  const route = { requests: 0, replies, onRequest: options.onRequest?.(destination) };
  const key = '/' + name; routes.set(key, route);
  const result = await shell('set -e; source "$1"; ensure_jar "$2" "$3"',
    [shellPath(helper), origin + key, shellPath(destination)], options.environment);
  const temporaryFiles = fs.readdirSync(folder).filter(file => file !== 'tool.jar');
  const receipt = { name, ...result, requests: route.requests, temporaryFiles,
    destinationExists: fs.existsSync(destination), bytes: fs.existsSync(destination) ? fs.statSync(destination).size : 0 };
  receipts.push(receipt);
  assert.deepEqual(temporaryFiles, [], 'A completed failure or success must retire every adjacent download temporary.');
  return { destination, result, route };
}

async function assertValid(destination, message) {
  const validation = await shell('jar tf "$1"', [shellPath(destination)]);
  assert.equal(validation.code, 0, message + ': ' + validation.stderr);
  assert.match(validation.stdout, /payload\.txt/);
  assert.deepEqual(fs.readFileSync(destination), validJar, 'The published archive must equal the successful HTTP response.');
}

test('downloads a valid archive through real curl and validates it with real jar', async () => {
  const row = await fixture('valid', ['valid']);
  assert.equal(row.result.code, 0); await assertValid(row.destination, 'A successful download must publish a valid archive');
  assert.equal(row.route.requests, 1);
});

for (const [kind, label] of [['503', 'transient HTTP503'], ['html', 'HTTP200 HTML'], ['truncated', 'truncated ZIP']]) {
  test('recovers from ' + label + ' before publishing a valid archive', async () => {
    const row = await fixture('recover-' + kind, [kind, 'valid']);
    assert.equal(row.result.code, 0); await assertValid(row.destination, 'Recovery from ' + label + ' must publish a readable JAR');
    assert.equal(row.route.requests, 2, 'Invalid HTTP status or archive must consume one bounded retry.');
  });
}

test('replaces a corrupt existing cache only after a validated download', async () => {
  const row = await fixture('corrupt-cache', ['valid'], { existing: Buffer.from('old broken archive') });
  assert.equal(row.result.code, 0); await assertValid(row.destination, 'A corrupt cache must be repaired');
  assert.equal(row.route.requests, 1);
});

test('reuses a valid existing cache without contacting a failing URL or replacing its bytes', async () => {
  const row = await fixture('valid-cache', ['503'], { existing: validJar });
  assert.equal(row.result.code, 0); assert.equal(row.route.requests, 0);
  await assertValid(row.destination, 'A valid cached archive must remain unchanged');
});

for (const kind of ['503', 'html', 'truncated']) {
  test('persistent ' + kind + ' fails after three attempts without publishing a destination or temporary', async () => {
    const row = await fixture('fail-' + kind, [kind]);
    assert.notEqual(row.result.code, 0); assert.equal(row.route.requests, 3);
    assert.equal(fs.existsSync(row.destination), false, 'An unvalidated response must never take the final cache name.');
  });
}

test('a failed repair preserves the original corrupt cache as evidence and cleans its temporary', async () => {
  const original = Buffer.from('retained corrupt cache');
  const row = await fixture('failed-repair', ['503'], { existing: original });
  assert.notEqual(row.result.code, 0); assert.equal(row.route.requests, 3);
  assert.deepEqual(fs.readFileSync(row.destination), original);
});

test('a failing download cannot replace a valid cache installed while its HTTP request was in flight', async () => {
  const row = await fixture('concurrent-valid', ['html'], {
    onRequest: destination => count => { if (count === 1) fs.writeFileSync(destination, validJar); }
  });
  assert.notEqual(row.result.code, 0); assert.equal(row.route.requests, 3);
  await assertValid(row.destination, 'Failed transfer must preserve the concurrently installed valid archive');
});

test('invalid timeout scalars reject before HTTP, cache publication or temporary creation', async () => {
  for (const [name, environment] of [
    ['colon', { IOS_JAR_CONNECT_TIMEOUT_SECONDS: '1:2' }],
    ['zero', { IOS_JAR_CONNECT_TIMEOUT_SECONDS: '0' }],
    ['overbound', { IOS_JAR_MAX_TIME_SECONDS: '121' }],
    ['huge', { IOS_JAR_MAX_TIME_SECONDS: '9'.repeat(256) }]
  ]) {
    const row = await fixture('timeout-' + name, ['valid'], { environment });
    assert.notEqual(row.result.code, 0); assert.equal(row.route.requests, 0);
    assert.equal(fs.existsSync(row.destination), false);
  }
});
