const { app, BrowserWindow, ipcMain, dialog, protocol, net, session, Menu, clipboard, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { createHash } = require('node:crypto');
const { EngineClient } = require('./engine-client.cjs');
const { engineOptions, resolveUserData } = require('./runtime.cjs');
const { productName, version } = require('./package.json');
const { commanderBrowse, publicDeckUrl } = require('./deck-sources.cjs');
const { createPreferences } = require('./preferences.cjs');

const project = path.resolve(__dirname, '..');
const userData = resolveUserData({ packaged: app.isPackaged, executable: process.execPath,
  appData: app.getPath('appData'), sourceDirectory: __dirname });
fs.mkdirSync(userData, { recursive: true });
const desktopLog = path.join(userData, 'desktop.log');
function diagnostic(message) {
  try { fs.appendFileSync(desktopLog, `${new Date().toISOString()} ${message}\n`); } catch { /* Retain Electron's original error handling. */ }
}
diagnostic(`Starting ${productName} ${version}; packaged=${app.isPackaged}`);
process.on('uncaughtExceptionMonitor', error => diagnostic(error.stack || String(error)));
const preferences = createPreferences(userData);
app.setPath('userData', userData);
app.setName(productName);
protocol.registerSchemesAsPrivileged([{ scheme: 'workshop', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

let window;
let engine;
let quitting = false;
const methods = new Set(['search', 'deckInsights', 'list', 'new', 'open', 'snapshot', 'edit', 'rename', 'undo', 'redo', 'format', 'save', 'importPreview', 'import', 'deckPresets', 'presetImport', 'export', 'practice', 'multiplayerHost', 'multiplayerJoin', 'multiplayerConfigure', 'multiplayerSelectDeck', 'multiplayerReady', 'multiplayerStart', 'multiplayerState', 'multiplayerReturn', 'multiplayerClose', 'matchOpponents', 'matchSetup', 'matchStart', 'matchState', 'matchAction', 'matchConcede']);
function verify(event) {
  if (!window || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame
    || !event.senderFrame.url.startsWith('workshop://app/')) throw new Error('Unknown desktop client');
}

const artCache = new Map();
let artQueue = Promise.resolve();
let lastArtRequest = 0;
const debugMethods = new Set(['multiplayerStart', 'multiplayerState', 'matchState']);
function summarizeDebug(method, result) {
  if (method === 'matchState') {
    if (!result) return 'null';
    return `status=${result.status || 'unknown'} players=${result.playerCount ?? 0} viewer=${result.viewerId ?? 'none'} turn=${result.turn ?? 'none'} phase=${result.phase || 'none'} error=${result.error || 'none'}`;
  }
  if (!result || typeof result !== 'object') return String(result);
  return `mode=${result.mode || 'none'} status=${result.status || 'none'} hosting=${Boolean(result.hosting)} matchActive=${Boolean(result.matchActive)} slots=${result.slots?.length ?? 0} error=${result.error || 'none'}`;
}
function art(name, face = 'front') {
  if (process.env.FORGE_OFFLINE === '1') return null;
  if (typeof name !== 'string' || name.length > 200) return null;
  if (face !== 'front' && face !== 'back') return null;
  // Preserve existing front-face downloads; backs have their own cache entry.
  const cacheName = face === 'back' ? `${name}\0back` : name;
  if (artCache.has(cacheName)) return artCache.get(cacheName);
  // Cached cards should never wait behind unrelated network downloads.
  const key = createHash('sha256').update(cacheName).digest('hex');
  const file = path.join(userData, 'art', `${key}.jpg`);
  if (fs.existsSync(file)) {
    const cached = fs.promises.readFile(file).then(bytes => 'data:image/jpeg;base64,' + bytes.toString('base64')).catch(() => null);
    artCache.set(cacheName, cached);
    return cached;
  }
  const promise = artQueue.then(async () => {
    await new Promise(resolve => setTimeout(resolve, Math.max(0, 150 - (Date.now() - lastArtRequest))));
    lastArtRequest = Date.now();
    try {
      const response = await fetch(`https://api.scryfall.com/cards/named?exact=${encodeURIComponent(name)}&format=image&version=normal&face=${face}`, {
        headers: { 'User-Agent': `ManaTable/${app.getVersion()} (https://github.com/proflayton/Mana-Table)`, Accept: 'image/jpeg' },
        signal: AbortSignal.timeout(8000)
      });
      if (!response.ok || !response.headers.get('content-type')?.startsWith('image/')) return null;
      const bytes = Buffer.from(await response.arrayBuffer());
      if (bytes.length > 2_000_000) return null;
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, bytes);
      return 'data:image/jpeg;base64,' + bytes.toString('base64');
    } catch { return null; }
  });
  artQueue = promise.catch(() => null);
  artCache.set(cacheName, promise);
  return promise;
}

if (process.env.MANA_ALLOW_MULTI_INSTANCE === '1' || app.requestSingleInstanceLock()) {
app.on('second-instance', () => {
  if (window) { if (window.isMinimized()) window.restore(); window.show(); window.focus(); }
});
app.whenReady().then(async () => {
  protocol.handle('workshop', request => {
    const pathname = new URL(request.url).pathname;
    // Ship the pinned renderer locally; no CDN or renderer network access.
    if (['/vendor/three.module.js', '/vendor/three.core.js'].includes(pathname)) {
      const file = path.basename(pathname);
      const directory = app.isPackaged ? path.join(__dirname, 'vendor', 'three') : path.join(__dirname, 'node_modules', 'three', 'build');
      return net.fetch(pathToFileURL(path.join(directory, file)).toString());
    }
    const allowed = new Set(['/index.html', '/style.css', '/app.js', '/presets.js', '/presets.css', '/match.js', '/match.css', '/battlefield.css', '/card-preview.js', '/card-preview.css', '/turn-guide.js', '/match-feedback.js', '/match-feedback.css', '/combat-view.js', '/combat-view.css', '/table-combat.js', '/table-combat.css', '/cast-view.js', '/cast-view.css', '/reveal-view.js', '/reveal-view.css', '/table-gestures.js', '/hand-view.js', '/hand-view.css', '/response-skip.js', '/play-preferences.js', '/table-card-preview.js', '/battlefield-view.js', '/deck-workshop.js', '/deck-workshop.css']);
    if (!allowed.has(pathname) && !['/table-scene.js', '/table-scene-world.mjs', '/table-world-layout.mjs', '/table-scene.css'].includes(pathname)) return new Response('Not found', { status: 404 });
    return net.fetch(pathToFileURL(path.join(__dirname, 'renderer', pathname.slice(1))).toString());
  });
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  Menu.setApplicationMenu(null);
  window = new BrowserWindow({
    width: 1540, height: 980, minWidth: 1000, minHeight: 740, backgroundColor: '#101415',
    title: `${productName} · Beta`, show: process.env.FORGE_TEST !== '1',
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, sandbox: true, nodeIntegration: false }
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.on('render-process-gone', (_event, details) => diagnostic(`Renderer exited: ${JSON.stringify(details)}`));
  engine = new EngineClient(engineOptions({ project, userData,
    resourcesPath: app.isPackaged ? process.resourcesPath : undefined }));
  engine.on('status', status => { if (!window.isDestroyed()) window.webContents.send('engine-status', status); });
  ipcMain.handle('status', event => { verify(event); return engine.status; });
  ipcMain.handle('preferences', (event, patch) => { verify(event); return patch === undefined ? preferences.get() : preferences.set(patch); });
  ipcMain.handle('engine', async (event, method, params) => {
    verify(event);
    if (!methods.has(method)) throw new Error('Unknown command');
    if (JSON.stringify(params).length > 1_500_000) throw new Error('Request too large');
    const shouldDebug = debugMethods.has(method);
    if (shouldDebug) console.log(`[mana-table] -> ${method}`);
    try {
      const result = await engine.request(method, params);
      if (shouldDebug) console.log(`[mana-table] <- ${method}: ${summarizeDebug(method, result)}`);
      return result;
    } catch (error) {
      diagnostic(`Engine command ${method} failed: ${error.stack || error}`);
      if (shouldDebug) console.log(`[mana-table] !! ${method}: ${error.message}`);
      throw error;
    }
  });
  ipcMain.handle('art', (event, name, face) => { verify(event); return art(name, face); });
  ipcMain.handle('browse-decks', async (event, destination) => {
    verify(event);
    let url;
    if (destination === 'updated' || destination === 'views') url = commanderBrowse(destination);
    else {
      const preset = (await engine.request('deckPresets')).find(preset => preset.id === destination);
      if (!preset) throw new Error('Unknown preset deck');
      url = publicDeckUrl(preset.moxfieldUrl);
    }
    await shell.openExternal(url);
    return true;
  });
  ipcMain.handle('copy-deck', async event => {
    verify(event);
    clipboard.writeText(await engine.request('export', { kind: 'text' }));
    return true;
  });
  ipcMain.handle('copy-invite', (event, value) => {
    verify(event);
    if (typeof value !== 'string' || value.length > 200 || /[\r\n\0]/.test(value)) throw new Error('Invalid invite');
    clipboard.writeText(value);
    return true;
  });
  ipcMain.handle('import-file', async event => {
    verify(event);
    const result = await dialog.showOpenDialog(window, { properties: ['openFile'], filters: [{ name: 'Deck lists', extensions: ['txt', 'dec', 'dck'] }] });
    if (result.canceled) return null;
    const file = result.filePaths[0];
    if (fs.statSync(file).size > 1_000_000) throw new Error('Deck files must be smaller than 1 MB');
    let text = fs.readFileSync(file, 'utf8');
    let name = path.basename(file, path.extname(file));
    if (path.extname(file).toLowerCase() === '.dck') {
      let metadata = false;
      text = text.split(/\r?\n/).filter(line => {
        if (/^\[metadata\]/i.test(line)) { metadata = true; return false; }
        if (/^\[/.test(line)) metadata = false;
        if (metadata && line.startsWith('Name=')) name = line.slice(5);
        return !metadata;
      }).join('\n');
    }
    return { text, name };
  });
  ipcMain.handle('export-file', async (event, kind) => {
    verify(event);
    if (!['text', 'forge'].includes(kind)) throw new Error('Unknown deck format');
    const text = await engine.request('export', { kind });
    const state = await engine.request('snapshot');
    const extension = kind === 'forge' ? 'dck' : 'txt';
    const result = await dialog.showSaveDialog(window, { defaultPath: state.deck.name.replace(/[<>:"/\\|?*]/g, '_') + '.' + extension,
      filters: [{ name: kind === 'forge' ? 'Forge deck' : 'Plain-text deck', extensions: [extension] }] });
    if (result.canceled) return false;
    await fs.promises.writeFile(result.filePath, text, 'utf8');
    return true;
  });
  await window.loadURL('workshop://app/index.html');
}).catch(error => {
  diagnostic(`Startup failed: ${error.stack || error}`);
  dialog.showErrorBox('Mana Table could not start', `${error.message}\n\nDetails: ${desktopLog}`);
  app.quit();
});
app.on('window-all-closed', () => app.quit());
app.on('before-quit', event => {
  if (quitting) return;
  quitting = true;
  event.preventDefault();
  const finish = async () => { await engine?.close(); app.quit(); };
  if (!engine) { finish(); return; }
  Promise.race([
    engine.request('multiplayerClose').catch(() => null),
    // Allow the router mapping to be withdrawn before terminating Java.
    new Promise(resolve => setTimeout(resolve, 10000))
  ]).finally(finish);
});
} else { app.quit(); }
