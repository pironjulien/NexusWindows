// Single owned diagnostic. It deliberately imports no BrowserWindow and never loads a page.
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), crypto = require('node:crypto');
const directory = path.join(os.homedir(), '.nexus', 'nexus-windows', 'qualification');
const candidates = process.argv.filter(arg => path.isAbsolute(arg) && path.basename(arg).startsWith('electron-headless-') && path.dirname(arg).toLowerCase() === directory.toLowerCase()).flatMap(state => {
  try {
    const marker = JSON.parse(fs.readFileSync(path.join(state, 'marker.json'), 'utf8'));
    return marker.owner === 'nexus-electron-headless-probe' && marker.script === __filename && marker.state === state ? [{ state, marker }] : [];
  } catch { return []; }
});
if (candidates.length !== 1) throw new Error('One exact owned headless probe marker is required');
const { state, marker } = candidates[0];
const allowed = new Set([process.execPath, __filename, marker.url, state, '--disable-features=CalculateNativeWinOcclusion', '--force-renderer-accessibility']);
const receipt = {
  run: marker.run, pid: process.pid, executable: process.execPath, at: new Date().toISOString(),
  argv: process.argv.map(arg => allowed.has(arg) ? arg : '[unexpected]'),
  argvShape: process.argv.map(arg => arg.startsWith('--') ? 'switch' : path.isAbsolute(arg) ? 'path' : /^http:/.test(arg) ? 'url' : 'other'),
  scriptReadSha256: crypto.createHash('sha256').update(fs.readFileSync(__filename)).digest('hex'),
  versions: { electron: process.versions.electron ?? null, node: process.versions.node }, runAsNode: Boolean(process.env.ELECTRON_RUN_AS_NODE),
};
function write(stage, extra = {}) { fs.writeFileSync(path.join(state, 'boot.json'), JSON.stringify({ ...receipt, stage, ...extra }, null, 2)); }
write('script-entered');
process.on('exit', code => fs.writeFileSync(path.join(state, 'exit.json'), JSON.stringify({ run: marker.run, pid: process.pid, code, at: new Date().toISOString() })));
if (process.argv.some(arg => !allowed.has(arg))) { write('arguments-refused'); process.exit(2); }
const { app } = require('electron');
if (!app) { write('not-electron-main'); process.exit(3); }
write('app-exit-requested', { appReady: app.isReady(), browserWindowImported: false, requestedExitCode: 0 });
app.exit(0);
