// Local, opt-in acceptance fixture. No DevTools switch, account, user profile or external navigation.
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
// Identify only this run's direct Temp directory before interpreting any positional argument. Electron can
// keep/reorder Chromium switches in argv; startup diagnostics must precede URL parsing or Electron imports.
const candidates = [...new Set(process.argv.filter(arg => path.isAbsolute(arg) && path.basename(arg).startsWith('nexus-simulated-live-')
  && path.dirname(path.resolve(arg)).toLowerCase() === path.resolve(os.tmpdir()).toLowerCase()))].flatMap(state => {
    try {
      if (fs.lstatSync(state).isSymbolicLink()) return [];
      const marker = JSON.parse(fs.readFileSync(path.join(state, '.nexus-simulated-fixture.json'), 'utf8'));
      return marker.owner === 'nexus-simulated-fixture' && marker.state === state && marker.script === __filename ? [{ state, marker }] : [];
    } catch { return []; }
  });
if (candidates.length !== 1) throw new Error('Exactly one owned fixture state marker must appear in argv; nothing opened');
const { state, marker } = candidates[0];
const startupFile = path.join(state, 'fixture-startup.jsonl');
const allowed = new Set([process.execPath, __filename, marker.url, state, '--disable-features=CalculateNativeWinOcclusion', '--force-renderer-accessibility']);
function record(stage, details = {}) { fs.appendFileSync(startupFile, JSON.stringify({ at: new Date().toISOString(), run: marker.run, pid: process.pid, stage, ...details }) + '\n'); }
record('module-enter', { argv: process.argv.map(arg => allowed.has(arg) ? arg : '[unexpected]'), argvShape: process.argv.map(arg => arg.startsWith('--') ? 'switch' : path.isAbsolute(arg) ? 'path' : /^http:/.test(arg) ? 'url' : 'other'), versions: { electron: process.versions.electron ?? null, node: process.versions.node, chrome: process.versions.chrome ?? null }, runAsNode: Boolean(process.env.ELECTRON_RUN_AS_NODE) });
if (process.argv.some(arg => !allowed.has(arg)) || [process.execPath, __filename, marker.url, state].some(arg => process.argv.filter(value => value === arg).length !== 1)) {
  record('arguments-refused', { unexpectedCount: process.argv.filter(arg => !allowed.has(arg)).length });
  throw new Error('Unknown or duplicate fixture arguments; nothing opened');
}
const url = new URL(marker.url);
if (url.hostname !== '127.0.0.1' || url.protocol !== 'http:' || url.username || url.password) { record('url-refused'); throw new Error('Exact local fixture URL required'); }
let app, window;
function fatal(error) { record('fatal', { name: error?.name, message: String(error?.message ?? error), stack: error?.stack }); process.exitCode = 1; if (app?.exit) app.exit(1); else process.exit(1); }
process.on('uncaughtException', fatal); process.on('unhandledRejection', fatal); process.on('exit', code => record('process-exit', { code }));
const electron = require('electron'); app = electron.app;
const { BrowserWindow } = electron;
if (!app || !BrowserWindow) throw new Error('The owned fixture needs the Electron main process, not run-as-node');
const profile = path.join(state, 'electron-profile');
fs.mkdirSync(profile, { recursive: true }); // app.setPath requires an existing directory, before app.ready.
app.setPath('userData', profile);
record('profile-configured', { profileExists: fs.statSync(profile).isDirectory() });
app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion');
app.commandLine.appendSwitch('disable-backgrounding-occluded-windows');
app.commandLine.appendSwitch('force-renderer-accessibility');
app.whenReady().then(() => {
  record('app-ready');
  app.setAccessibilitySupportEnabled(true);
  window = new BrowserWindow({ width: 1000, height: 900, show: false,
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true, backgroundThrottling: false } });
  const handle = window.getNativeWindowHandle(), hwnd = Number(handle.length === 8 ? handle.readBigUInt64LE() : handle.readUInt32LE());
  record('window-created', { hwnd });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, destination) => { if (new URL(destination).origin !== url.origin) event.preventDefault(); });
  window.webContents.on('did-fail-load', (_event, code, description, _url, isMainFrame) => record('did-fail-load', { code, description, isMainFrame }));
  window.webContents.on('render-process-gone', (_event, details) => record('render-process-gone', { reason: details.reason, exitCode: details.exitCode }));
  window.webContents.on('did-finish-load', () => record('did-finish-load', { exactUrl: window.webContents.getURL() === url.toString() }));
  window.once('ready-to-show', () => {
    record('ready-to-show', { hwnd }); window.showInactive(); window.moveBottom?.();
    const ready = { run: marker.run, pid: process.pid, hwnd, visible: window.isVisible(), focused: window.isFocused(), url: url.toString() };
    fs.writeFileSync(path.join(state, 'fixture-main-ready.json'), JSON.stringify(ready)); record('shown-inactive', ready);
  });
  window.once('closed', () => { record('window-closed'); window = null; });
  window.loadURL(url.toString()).catch(fatal);
}).catch(fatal);
app.on('child-process-gone', (_event, details) => record('child-process-gone', { type: details.type, reason: details.reason, exitCode: details.exitCode }));
app.on('before-quit', () => record('before-quit'));
app.on('window-all-closed', () => { record('window-all-closed'); app.quit(); });
