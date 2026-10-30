// Contrats sans interface : garde réelle de session et routage MCP (les plans du moteur natif sont vérifiés par
// tests/WindowTests.cs, xUnit).
import test from 'node:test';
import assert from 'node:assert/strict';
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
