const { test, expect } = require('@playwright/test');
const { launchDesktop } = require('./support/desktop.cjs');

test('reopening while the old window has closed queues one relaunch without touching the destroyed window', async () => {
  const { application } = await launchDesktop('reopen-shutdown');
  try {
    const page = await application.firstWindow();
    await expect(page.locator('#loading')).toBeHidden({ timeout: 60000 });
    await application.evaluate(({ app, BrowserWindow }) => {
      globalThis.reopenTest = { quits: 0, relaunches: 0, originalRelaunch: app.relaunch };
      globalThis.reopenTest.hold = event => { event.preventDefault(); globalThis.reopenTest.quits++; };
      app.on('before-quit', globalThis.reopenTest.hold);
      app.relaunch = () => { globalThis.reopenTest.relaunches++; };
      BrowserWindow.getAllWindows()[0].destroy();
    });
    // Keep the process alive across the real asynchronous shutdown path, just
    // as a new launch can arrive while lobby cleanup is still in progress.
    await expect.poll(() => application.evaluate(() => globalThis.reopenTest.quits)).toBeGreaterThanOrEqual(1);
    const queued = await application.evaluate(({ app }) => {
      app.emit('second-instance', {}, [], process.cwd());
      app.emit('second-instance', {}, [], process.cwd());
      return globalThis.reopenTest.relaunches;
    });
    expect(queued).toBe(1);
    await expect.poll(() => application.evaluate(() => globalThis.reopenTest.quits), { timeout: 20000 }).toBeGreaterThanOrEqual(2);
  } finally {
    await application.evaluate(({ app }) => {
      if (!globalThis.reopenTest) return;
      app.removeListener('before-quit', globalThis.reopenTest.hold);
      app.relaunch = globalThis.reopenTest.originalRelaunch;
    });
    await application.close();
  }
});
