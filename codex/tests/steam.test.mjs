// Contrats Steam sans GUI : fichiers factices pour l'identite, arbre de processus fourni au helper,
// serveur WebSocket local pour la preuve CDP. Aucun lancement/arret du vrai client.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile, execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools, TOOLS } from '../lib/mcp.mjs';
import { actionTimeoutMs, STEAM_LAUNCH_TIMEOUT_MS } from '../lib/native.mjs';

const executable = process.env.NEXUS_FOREGROUND_EXE;
const skip = !executable && 'NEXUS_FOREGROUND_EXE absent';
const contract = cases => JSON.parse(execFileSync(executable, ['--steam-selftest'], { input: JSON.stringify(cases), windowsHide: true }).toString());
function bench(t) {
  const root = mkdtempSync(join(tmpdir(), 'nexus-steam-contract-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = path => { mkdirSync(join(path, '..'), { recursive: true }); writeFileSync(path, ''); return path; };
  const exe = file(join(root, 'Valve', 'steam.exe'));
  file(join(root, 'Valve', 'steamclient64.dll'));
  return { root, exe, fake: file(join(root, 'fake', 'steam.exe')), editor: file(join(root, 'Valve', 'editor.exe')) };
}

test('Steam startup uses its CEF view flags; debugging needs the explicit option and an actual client identity', { skip }, t => {
  const b = bench(t);
  const [plain, debugging, fake, editor] = contract([
    { kind: 'plan', exe: b.exe }, { kind: 'plan', exe: b.exe, args: '-silent steam://open/library', devtools: true },
    { kind: 'plan', exe: b.fake }, { kind: 'plan', exe: b.editor },
  ]);
  assert.equal(plain.arguments, '-cef-disable-occlusion -cef-force-accessibility');
  assert.equal(debugging.arguments, '-silent steam://open/library -cef-disable-occlusion -cef-force-accessibility -cef-enable-debugging');
  assert.match(fake.refused, /not the genuine Steam/);
  assert.match(editor.refused, /not the genuine Steam/);
  const [normal] = JSON.parse(execFileSync(executable, ['--launch-selftest'], { input: JSON.stringify([{ target: b.editor, args: 'note.txt' }]), windowsHide: true }).toString());
  assert.equal(normal.arguments, 'note.txt');
});

test('native Steam startup requests SW_SHOWNOACTIVATE in a real helper child, with no GUI', { skip }, () => {
  const [startup] = contract([{ kind: 'startup' }]);
  assert.ok(startup.flags & 1);
  assert.equal(startup.showWindow, 4);
});

test('native process identity reports a real UTC creation timestamp and never invents an unknown identity', { skip }, () => {
  const before = Date.now();
  const identity = JSON.parse(execFileSync(executable, ['--process-identity-selftest'], { windowsHide: true }).toString());
  assert.ok(Number.isSafeInteger(identity.pid) && identity.pid > 0);
  assert.match(identity.processStartedUtc, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/);
  assert.ok(Date.parse(identity.processStartedUtc) <= Date.now());
  assert.ok(Date.parse(identity.processStartedUtc) >= before - 5000);
  assert.equal(identity.unknown, null);
});

test('raw control/profile switches, neutralizers and closing URIs cannot bypass Steam launch options', { skip }, t => {
  const b = bench(t);
  const rejected = [
    '-cef-enable-debugging', '--remote-debugging-port=8080', '/remote-debugging-address=0.0.0.0', '--remote-debugging-pipe',
    '--user-data-dir=C:\\x', '--profile-directory=Default', '--', '--single-argument', '-shutdown', '/restart',
    'steam://exit', 'steam://quit', 'steam://shutdown', 'steam://restart',
  ];
  assert.ok(contract(rejected.map(args => ({ kind: 'plan', exe: b.exe, args }))).every(r => r.refused));
  const [ordinary] = contract([{ kind: 'plan', exe: b.exe, args: '-silent -cef-force-accessibility steam://open/main' }]);
  assert.match(ordinary.arguments, /^-silent -cef-force-accessibility steam:\/\/open\/main /);
});

test('Steam restart inventory finds nested games and foreign helpers even when they borrow an internal name', { skip }, t => {
  const b = bench(t);
  const processes = [
    { pid: 1, parent: 0, name: 'steam.exe', path: b.exe },
    { pid: 2, parent: 1, name: 'steamwebhelper.exe', path: join(b.root, 'Valve', 'bin', 'steamwebhelper.exe') },
    { pid: 3, parent: 2, name: 'game.exe', path: join(b.root, 'games', 'game.exe') },
    { pid: 4, parent: 3, name: 'launcher.exe', path: join(b.root, 'games', 'launcher.exe') },
    { pid: 5, parent: 1, name: 'steamwebhelper.exe', path: join(b.root, 'fake', 'steamwebhelper.exe') },
    { pid: 6, parent: 1, name: 'steamservice.exe' }, // illisible : jamais considere interne
    { pid: 7, parent: 99, name: 'unrelated.exe', path: join(b.root, 'unrelated.exe') },
  ];
  const [result] = contract([{ kind: 'tree', exe: b.exe, roots: [1], processes }]);
  assert.deepEqual(result.descendants, [2, 5, 6, 3, 4]);
  assert.deepEqual(result.games, ['game.exe', 'launcher.exe', 'steamservice.exe', 'steamwebhelper.exe']);
});

test('Steam window selection accepts one main window and refuses ambiguous titled or fallback candidates', { skip }, () => {
  const results = contract([[], ['Sign in'], ['Library', 'Steam'], ['Steam', ' Steam '], ['Store', 'Friends']]
    .map(titles => ({ kind: 'window', titles })));
  assert.deepEqual(results.slice(0, 3), [{ index: -1 }, { index: 0 }, { index: 1 }]);
  assert.ok(results.slice(3).every(r => /ambiguous/.test(r.refused)));
});

test('Steam reuse preserves ordinary args once and checks DevTools before transmitting a command', { skip }, () => {
  const results = contract([
    { kind: 'reuse', titles: ['Steam'], args: '-applaunch 42' },
    { kind: 'reuse', titles: ['Steam'] }, { kind: 'reuse', titles: [] },
    { kind: 'reuse', titles: ['Steam'], args: 'steam://open/library', devtools: true },
    { kind: 'reuse', titles: ['Steam'], args: 'steam://open/library', devtools: true, port: 8080 },
  ]);
  assert.deepEqual(results.slice(0, 3), [{ command: '-applaunch 42' }, { command: null }, { command: 'steam://open/main' }]);
  assert.match(results[3].refused, /already running without a verified/);
  assert.deepEqual(results[4], { command: 'steam://open/library' });
});

test('Steam DevTools requires its own helper on loopback and a matching local WebSocket endpoint', { skip }, () => {
  const endpoint = 'ws://127.0.0.1:8080/devtools/browser/id';
  const listeners = [
    [{ address: '127.0.0.1', pid: 2 }], [{ address: '127.0.0.1', pid: 9 }], [{ address: '0.0.0.0', pid: 2 }],
    [{ address: '127.0.0.1', pid: 2 }, { address: '::', pid: 2 }], [],
  ];
  const owned = contract(listeners.map(listeners => ({ kind: 'endpoint', helpers: [2], listeners, reply: { webSocketDebuggerUrl: endpoint } })));
  assert.deepEqual(owned.map(r => r.owned), [true, false, false, false, false]);
  const wrong = ['ws://foreign.test:8080/id', 'ws://127.0.0.1:8081/id', 'http://127.0.0.1:8080/id', 'ws://user@127.0.0.1:8080/id', 'ws://127.0.0.1:8080/'];
  assert.ok(contract(wrong.map(url => ({ kind: 'endpoint', helpers: [2], listeners: listeners[0], reply: { webSocketDebuggerUrl: url } }))).every(r => !r.cdp));
});

test('Steam CDP liveness exchanges Browser.getVersion; a listening socket or unrelated JSON does not prove CDP', { skip }, async t => {
  let response = { result: { protocolVersion: '1.3', product: 'Chrome/contract' } };
  const methods = [], sockets = new Set();
  const server = createServer();
  server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
  server.on('upgrade', (request, socket) => {
    const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let buffer = Buffer.alloc(0);
    socket.on('data', part => {
      buffer = Buffer.concat([buffer, part]);
      if (buffer.length < 6) return;
      const length = buffer[1] & 127;
      if (length >= 126 || buffer.length < 6 + length) return;
      const payload = Buffer.from(buffer.subarray(6, 6 + length));
      for (let i = 0; i < payload.length; i++) payload[i] ^= buffer[2 + i % 4];
      buffer = buffer.subarray(6 + length);
      let message; try { message = JSON.parse(payload.toString()); } catch { return; }
      methods.push(message.method);
      if (response === null) return;
      const body = Buffer.from(JSON.stringify({ id: message.id, ...response }));
      assert.ok(body.length < 126);
      socket.write(Buffer.concat([Buffer.from([0x81, body.length]), body]));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); });
  const run = async () => {
    const child = execFile(executable, ['--steam-selftest'], { windowsHide: true });
    child.stdin.end(JSON.stringify([{ kind: 'cdp', url: `ws://127.0.0.1:${server.address().port}/devtools/browser/id` }]));
    const stdout = await new Promise((resolve, reject) => {
      let out = ''; child.stdout.on('data', data => { out += data; }); child.on('error', reject); child.on('close', code => code === 0 ? resolve(out) : reject(new Error(`selftest ${code}`)));
    });
    return JSON.parse(stdout)[0].alive;
  };
  assert.equal(await run(), true);
  response = { result: { hello: 'ordinary JSON' } }; assert.equal(await run(), false);
  response = { error: { message: 'unsupported method' } }; assert.equal(await run(), false);
  response = null; assert.equal(await run(), false);
  assert.deepEqual(methods, Array(4).fill('Browser.getVersion'));
});

test('MCP launch passes explicit Steam options, preserves attested reuse metadata, and rejects malformed options before input', async () => {
  const sent = [], steam = { reused: true, restarted: false, devtoolsRequested: true, devtoolsPort: 8080, viewSwitchesApplied: false, notes: [] };
  const native = { async launch(target, options) { sent.push({ target, options }); return { opened: true, windows: [{ id: 7, proof: 'steam' }], steam }; }, async withForeground(_, run) { return run(); } };
  const session = new WindowsSession({ target: 'windows', async list_windows() { return []; } }, { native });
  try {
    const tools = new WindowsTools(session);
    const result = await tools.call('launch', { app: 'Steam', args: '-silent', devtools: true, restart: false });
    assert.deepEqual(sent, [{ target: 'Steam', options: { args: '-silent', devtools: true, restart: false } }]);
    assert.deepEqual(result.structuredContent.steam, steam);
    await assert.rejects(session.launch('Steam', '', { restart: 'yes' }), /booleans/);
    await assert.rejects(session.launch('ms-settings:display', '', { devtools: true }), /only for Steam/);
    assert.equal(sent.length, 1);
    const schema = TOOLS.find(t => t.name === 'launch').inputSchema;
    assert.equal(schema.properties.devtools.type, 'boolean'); assert.equal(schema.properties.restart.type, 'boolean');
    assert.ok(actionTimeoutMs('launch', {}, 100) >= STEAM_LAUNCH_TIMEOUT_MS);
    assert.ok(STEAM_LAUNCH_TIMEOUT_MS > 8000 + 60000 + 90000 + 10000);
  } finally { await session.close(); }
});
