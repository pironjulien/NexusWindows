// Contrats sans interface : garde réelle de session, routage MCP, et plans du helper compilé.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools, TOOLS, schemaErrors } from '../lib/mcp.mjs';
import { windowRequest, WINDOW_ACTIONS } from '../lib/window-management.mjs';

test('window contract rejects malformed placement before the foreground token', () => {
  for (const [action, args] of [['front', {}], ['move', { x: 4 }], ['resize', { width: 0, height: 8 }],
    ['move', { x: 1.5, y: 0 }], ['restore', { x: 0 }], ['minimize', { foreground: 'yes' }]]) {
    assert.throws(() => windowRequest(action, args), e => e.beforeInput === true);
  }
  assert.deepEqual(windowRequest('move', { x: -200, y: 80 }), { action: 'move', x: -200, y: 80 });
  assert.deepEqual(TOOLS.find(t => t.name === 'window').inputSchema.properties.action.enum, WINDOW_ACTIONS);
  assert.equal(schemaErrors(TOOLS.find(t => t.name === 'window').inputSchema, { action: 'resize', width: 640, height: 480 }).length, 0);
});

const target = { id: 7, app: 'fixture', title: 'Fixture', pid: 123 };
function fixture({ active = () => false, stopped = () => false, refuseToken = false, failCall = false } = {}) {
  const calls = [], order = [];
  const sky = { target: 'windows', async list_windows() { return [target]; }, async get_window() { return target; } };
  const stop = { assertClear() { if (stopped()) throw Error('STOP'); }, presence: {
    available: true, source: 'raw-input', idleMs: () => 10000, now: () => 10, since: active,
    async waitForPause() { order.push('pause'); return { waitedMs: 5 }; },
  } };
  const native = {
    async withForeground(_, run, opts) {
      if (refuseToken) throw Object.assign(Error('token busy'), { deferred: true });
      order.push('token');
      try { return await run({ held: () => true }); }
      finally { order.push('release'); opts.onRelease(opts.keep() ? 'kept-by-julien' : 'restored'); }
    },
    async call(window, op, args) {
      assert.equal(order.at(-1), 'token'); calls.push({ window, op, args });
      if (failCall) throw Object.assign(Error('Window disappeared after command'), { afterInput: true });
      return { status: 'verified', state: 'open', action: args.action, verified: true, performed: true,
        bounds: { left: 0, top: 0, width: 640, height: 480 }, foregroundTaken: true, technique: 'background:win32' };
    },
  };
  return { session: new WindowsSession(sky, { native, emergencyStop: stop }), calls, order };
}

test('front is explicitly kept; temporary window actions return foreground under the token', async () => {
  const { session, calls, order } = fixture();
  try {
    await session.bind({ id: 7 });
    await assert.rejects(session.manageWindow('front'), /foreground:true/);
    assert.equal(calls.length, 0);
    const front = await session.manageWindow('front', { foreground: true });
    assert.equal(front.foregroundReturned, 'kept-requested');
    assert.equal(front.waitedMs, 5);
    const resize = await session.manageWindow('resize', { width: 640, height: 480 });
    assert.equal(resize.foregroundReturned, 'restored');
    assert.deepEqual(order, ['pause', 'token', 'release', 'pause', 'token', 'release']);
    assert.deepEqual(calls.map(c => c.op), ['manageWindow', 'manageWindow']);
  } finally { await session.close(); }
});

test('Julien resuming input is distinguished from a requested persistent front', async () => {
  const { session } = fixture({ active: () => true });
  try { await session.bind({ id: 7 }); assert.equal((await session.manageWindow('front', { foreground: true })).foregroundReturned, 'kept-by-julien'); }
  finally { await session.close(); }
});

test('STOP or a busy token prevents the window command', async () => {
  for (const options of [{ stopped: () => true }, { refuseToken: true }]) {
    const { session, calls } = fixture(options);
    try { await session.bind({ id: 7 }); await assert.rejects(session.manageWindow('maximize'), /STOP|token busy/); assert.equal(calls.length, 0); }
    finally { await session.close(); }
  }
});

test('MCP placement routes exact arguments and retains the close API', async () => {
  const calls = [];
  const session = { window: null, async windows() { return [target]; },
    async bind(window) { this.window = window; return window; },
    async manageWindow(action, args) { calls.push([action, args]); return { status: 'verified', state: 'open', action }; },
    async closeWindow() { calls.push(['close']); return { status: 'verified', state: 'closed', prompts: [] }; } };
  const tools = new WindowsTools(session);
  await tools.call('window', { action: 'move', window: 7, x: -12, y: 25 });
  await tools.call('window', { action: 'close', window: 7 });
  assert.deepEqual(calls, [['move', { x: -12, y: 25 }], ['close']]);
});

test('failure after a window command is unknown and cannot produce a replay', async () => {
  const { session, calls, order } = fixture({ failCall: true });
  try {
    await session.bind({ id: 7 });
    const result = await session.manageWindow('maximize');
    assert.equal(result.status, 'sent-unconfirmed');
    assert.equal(result.state, 'unknown');
    assert.equal(calls.length, 1);
    assert.deepEqual(order, ['pause', 'token', 'release']);
  } finally { await session.close(); }
});

const executable = process.env.NEXUS_FOREGROUND_EXE;
test('native window proof uses placement and state rather than a changed image', { skip: !executable && 'NEXUS_FOREGROUND_EXE absent' }, () => {
  const before = { visible: true, minimized: false, maximized: false, foreground: false, bottom: false, left: 20, top: 30, width: 600, height: 400 };
  const entry = (request, after) => ({ request, before, after: { ...before, ...after } });
  const cases = [entry({ action: 'move', x: 50, y: 60 }, { left: 50, top: 60 }),
    entry({ action: 'move', x: 50, y: 60 }, { left: 50, top: 60, width: 601 }),
    entry({ action: 'resize', width: 800, height: 500 }, { width: 800, height: 500 }),
    entry({ action: 'resize', width: 800, height: 500 }, { left: 21, width: 800, height: 500 }),
    entry({ action: 'restore' }, { minimized: true }), entry({ action: 'maximize' }, { maximized: true }),
    entry({ action: 'front', foreground: true }, { foreground: false }), entry({ action: 'front' }, {})];
  const results = JSON.parse(execFileSync(executable, ['--window-selftest'], { input: JSON.stringify(cases), windowsHide: true, timeout: 10000 }));
  assert.deepEqual(results.slice(0, -1).map(r => r.reached), [true, false, true, false, false, true, false]);
  assert.match(results.at(-1).error, /foreground:true/);
});
test('native semantic wheel accepts fresh relocation and drag remains refused', { skip: !executable && 'NEXUS_FOREGROUND_EXE absent' }, () => {
  const results = JSON.parse(execFileSync(executable, ['--physical-target-selftest'], {
    input: JSON.stringify(['click', 'move', 'wheel', 'drag', 'key', 'type']), windowsHide: true, timeout: 10000 }));
  assert.deepEqual(results, [true, true, true, false, false, false]);
});

test('native bottom permits only public shell identities and follows their entire chain', { skip: !executable && 'NEXUS_FOREGROUND_EXE absent' }, () => {
  const entry = (chain, extra = {}) => ({ target: 1, shell: 2, desktop: 3, chain, ...extra });
  const results = JSON.parse(execFileSync(executable, ['--window-selftest'], {
    input: JSON.stringify([
      entry({ 1: 0 }), entry({ 1: 2, 2: 0 }), entry({ 1: 3, 3: 0 }), entry({ 1: 2, 2: 3, 3: 0 }),
      entry({ 1: 4, 4: 0 }, { visible: false }), // vraie application masquée
      entry({ 1: 4, 4: 0 }, { className: 'Progman', process: 'explorer.exe' }), // homonyme sans identité API
      entry({ 1: 2, 2: 4, 4: 0 }), // l'application sous le shell n'est pas ignorée
      entry({ 1: 2, 2: 3, 3: 2 }), entry({ 1: 1 }), // cycles
      entry({ 1: 4 }, { shell: 0, desktop: 0 }),
    ]), windowsHide: true, timeout: 10000 }));
  assert.deepEqual(results.map(result => result.bottom), [true, true, true, true, false, false, false, false, false, false]);
});
