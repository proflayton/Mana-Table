const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { snapshotEngineJar } = require('./runtime.cjs');

class EngineClient extends EventEmitter {
  constructor({ java, jar, resources, data, log, isolateJar = false, testMode = false }) {
    super();
    this.pending = new Map();
    this.sequence = 0;
    this.status = { state: 'loading', message: 'Waking up the card library…' };
    fs.mkdirSync(path.dirname(log), { recursive: true });
    const snapshot = isolateJar ? snapshotEngineJar(jar) : null;
    this.log = fs.createWriteStream(log, { flags: 'a' });
    this.child = spawn(java, ['-Xmx2g', '-Dfile.encoding=UTF-8', '-Djava.awt.headless=true', ...(testMode ? ['-Dmana.test=true'] : []), '-jar', snapshot?.jar || jar, resources, data],
      { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.once('close', () => {
      try { snapshot?.dispose(); }
      catch (error) { this.log.write(`Could not remove temporary engine: ${error.message}\n`); }
      this.log.end();
    });
    this.child.stderr.pipe(this.log);
    createInterface({ input: this.child.stdout }).on('line', line => {
      let message;
      try { message = JSON.parse(line); } catch { this.log.write(line + '\n'); return; }
      if (message.event) {
        this.status = { state: message.event, message: message.message, printings: message.printings };
        this.emit('status', this.status);
      } else {
        const pending = this.pending.get(message.id);
        if (!pending) return;
        clearTimeout(pending.timer);
        this.pending.delete(message.id);
        if (message.error) pending.reject(new Error(message.error));
        else pending.resolve(message.result);
      }
    });
    this.child.on('error', error => this.fail(`Could not start the engine: ${error.message}`));
    this.child.stdin.on('error', error => this.fail(`The engine connection closed: ${error.message}`));
    this.child.on('exit', code => this.fail(`The engine stopped (${code ?? 'closed'}). Relaunch the app to reconnect.`));
  }
  fail(message) {
    this.status = { state: 'error', message };
    this.emit('status', this.status);
    for (const pending of this.pending.values()) { clearTimeout(pending.timer); pending.reject(new Error(message)); }
    this.pending.clear();
  }
  request(method, params = {}) {
    if (this.status.state !== 'ready') return Promise.reject(new Error(this.status.state === 'error'
      ? this.status.message : 'The card library is still loading.'));
    return new Promise((resolve, reject) => {
      const id = ++this.sequence;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('The engine took too long. Your saved decks are safe; relaunch to reconnect.')); }, 30000);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ id, method, params }) + '\n', error => {
        if (error) { clearTimeout(timer); this.pending.delete(id); reject(error); }
      });
    });
  }
  close() {
    if (!this.child || this.child.exitCode !== null) return Promise.resolve();
    return new Promise(resolve => {
      let settled = false;
      let forceKill;
      const finish = () => {
        if (settled) return;
        settled = true;
        clearTimeout(forceKill);
        resolve();
      };
      this.child.once('exit', finish);
      this.child.stdin.end();
      forceKill = setTimeout(() => {
        if (this.child.exitCode === null) this.child.kill();
        setTimeout(finish, 500).unref();
      }, 1500);
      forceKill.unref();
    });
  }
}
module.exports = { EngineClient };
