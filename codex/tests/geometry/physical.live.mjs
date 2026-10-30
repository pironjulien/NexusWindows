// Explicit opt-in: only self-created fixture surfaces receive input. The real
// shared foreground token, stop latch and Raw Input presence guard remain active.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { NativeUIA } from '../../lib/native.mjs';
import { EmergencyStop } from '../../lib/stop.mjs';

const helper = process.env.NEXUS_NATIVE_TEST_EXE;
const executable = process.env.NEXUS_GEOMETRY_FIXTURE;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const skip = process.platform !== 'win32' || !helper || !executable || process.env.NEXUS_NATIVE_PHYSICAL_TEST !== '1';

function fixture(args = []) {
  const child = spawn(executable, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = [], waiters = []; let serial = 0, stderr = '';
  child.stderr.on('data', c => { stderr += c; });
  readline.createInterface({ input: child.stdout }).on('line', text => {
    const value = JSON.parse(text); lines.push(value);
    for (const pending of [...waiters]) if (pending.match(value)) { clearTimeout(pending.timer); waiters.splice(waiters.indexOf(pending), 1); pending.resolve(value); }
  });
  function wait(match) {
    const old = lines.find(match); if (old) return Promise.resolve(old);
    return new Promise((resolve, reject) => {
      const pending = { match, resolve, timer: setTimeout(() => { waiters.splice(waiters.indexOf(pending), 1); reject(new Error(`Fixture timeout ${stderr}`)); }, 10000) }; waiters.push(pending);
    });
  }
  return { child, lines, wait, async command(command, values = {}) {
    const id = String(++serial), result = wait(x => x.id === id);
    child.stdin.write(JSON.stringify({ command, id, ...values }) + '\n'); return result;
  } };
}

function decode(capture) {
  // Pillow is an independent real WebP decoder, not an inspection of a RIFF header.
  const result = spawnSync(process.env.NEXUS_TEST_PYTHON ?? 'python', ['-c', 'import json,sys; from PIL import Image; im=Image.open(sys.argv[1]).convert("RGB"); im.load(); print(json.dumps({"size":im.size,"center":im.getpixel((im.width//2,im.height//2))}))', capture.path], { windowsHide: true, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const decoded = JSON.parse(result.stdout);
  assert.deepEqual(decoded.size, [capture.width, capture.height]);
  assert.deepEqual(decoded.center, [100, 149, 237], 'the target pixels are blue, including when a red sibling covers it');
  return decoded;
}

async function controlled(args, body) {
  const form = fixture(args), stop = new EmergencyStop();
  const native = new NativeUIA(helper, { emergencyStop: stop });
  const captures = await mkdtemp(path.join(os.tmpdir(), 'nexus-geometry-'));
  try {
    const ready = await form.wait(x => x.id === 'ready');
    const candidates = (await native.listWindows(form.child.pid)).windows.filter(w => w.id === ready.hwnd);
    assert.equal(candidates.length, 1); const window = candidates[0];
    stop.startGuard(helper);
    for (let i = 0; i < 50 && stop.presence.source !== 'raw-input'; i++) await delay(100);
    assert.equal(stop.presence.source, 'raw-input');
    const lease = async action => {
      await stop.presence.waitForPause(); stop.assertClear();
      const began = stop.presence.now();
      let returned;
      const result = await native.withForeground('Geometry controlled fixture', async () => {
        stop.assertClear(); await stop.presence.waitForPause();
        if (stop.presence.source !== 'raw-input') throw new Error('Reliable presence lost');
        return action();
      }, { window, keep: () => stop.presence.since(began), onRelease: value => { returned = value; } });
      return { result, returned };
    };
    await body({ form, native, captures, window, ready, lease });
  } finally { native.close(); stop.close(); form.child.stdin.end(); form.child.kill(); await rm(captures, { recursive: true, force: true }); }
}

function point(capture) {
  const f = capture.windowFrame;
  return { action: 'click', x: f.left + Math.floor(f.width / 2), y: f.top + Math.floor(f.height / 2), frame: [f.left, f.top, f.width, f.height] };
}

test('physical coordinates reject an activation resize before sending input', { skip, timeout: 40000 }, async t => {
  const form = fixture(); const stop = new EmergencyStop();
  const native = new NativeUIA(helper, { emergencyStop: stop });
  const captures = await mkdtemp(path.join(os.tmpdir(), 'nexus-geometry-'));
  try {
    const ready = await form.wait(x => x.id === 'ready');
    const candidates = (await native.listWindows(form.child.pid)).windows.filter(w => w.id === ready.hwnd);
    assert.equal(candidates.length, 1); const window = candidates[0];
    stop.startGuard(helper);
    for (let i = 0; i < 50 && stop.presence.source !== 'raw-input'; i++) await delay(100);
    assert.equal(stop.presence.source, 'raw-input');
    const image = await native.capture(window, captures);
    const frame = image.windowFrame; assert.ok(frame);
    await form.command('resizeOnActivate');
    await stop.presence.waitForPause(); stop.assertClear();
    let returned;
    const previous = await native.probe();
    let refusal;
    try { await native.withForeground('Geometry activation-resize regression fixture', async () => {
      stop.assertClear(); await stop.presence.waitForPause();
      return native.call(window, 'physical', { action: 'click', x: frame.left + Math.floor(frame.width / 2), y: frame.top + Math.floor(frame.height / 2), frame: [frame.left, frame.top, frame.width, frame.height] });
    }, { window, onRelease: value => { returned = value; } }); } catch (error) { refusal = error; }
    const after = await form.command('state');
    t.diagnostic(JSON.stringify({ fixtureAwareness: ready.awareness, frame, afterWidth: after.width, clicks: after.clicks, returned, refusal: refusal?.message ?? null }));
    assert.match(refusal?.message ?? '', /Window moved or resized/);
    assert.equal(after.clicks, 0, 'activation resize must not send a click at the stale location');
    assert.equal(after.overlayClicks, 0);
    assert.ok(form.lines.some(x => x.eventName === 'activationResize'), 'the application actually resized during activation');
    const current = await native.probe();
    assert.equal(current.foreground, previous.foreground, 'previous foreground restored');
    assert.equal(returned, 'restored');
  } finally { native.close(); stop.close(); form.child.stdin.end(); form.child.kill(); await rm(captures, { recursive: true, force: true }); }
});

for (const profile of ['per-monitor', 'unaware']) test(`physical geometry: ${profile} target, covered capture, topmost refusal, maximized state and foreground return`, { skip, timeout: 50000 }, async t => {
  await controlled(profile === 'unaware' ? ['--unaware'] : [], async ({ form, native, captures, window, ready, lease }) => {
    const before = await native.probe();
    const capture = await native.capture(window, captures); decode(capture);
    const click = await lease(() => native.call(window, 'physical', point(capture)));
    assert.equal(click.returned, 'restored');
    assert.equal((await form.command('state')).clicks, 1, 'a real click event arrived in the fixture');
    assert.equal((await native.probe()).foreground, before.foreground);

    // A same-process topmost sibling must not pass OfTarget: owning the target process is insufficient.
    await form.command('overlay');
    const covered = await native.capture(window, captures); decode(covered);
    await assert.rejects(lease(() => native.call(window, 'physical', point(covered))), /does not hit the target window/);
    const blocked = await form.command('state');
    assert.equal(blocked.clicks, 1); assert.equal(blocked.overlayClicks, 0, 'no event injected into the overlay');
    await form.command('uncover');
    assert.equal((await native.probe()).foreground, before.foreground);

    await lease(() => form.command('maximize'));
    const maximized = await form.command('state'); assert.equal(maximized.state, 'Maximized');
    const big = await native.capture(window, captures); decode(big);
    await lease(() => native.call(window, 'physical', point(big)));
    const kept = await form.command('state');
    assert.equal(kept.state, 'Maximized', 'activation must not restore a maximized window');
    assert.equal(kept.width, maximized.width); assert.equal(kept.height, maximized.height);
    assert.equal(kept.clicks, 2, 'maximized target received exactly one additional click');
    assert.equal((await native.probe()).foreground, before.foreground);

    // Stale before activation: refuse without activating the window or injecting anything.
    await lease(() => form.command('normal'));
    const old = await native.capture(window, captures);
    const activations = form.lines.filter(x => x.eventName === 'activated').length;
    await form.command('move', { x: ready.left + 20, y: ready.top + 20 });
    await assert.rejects(lease(() => native.call(window, 'physical', point(old))), /Window moved or resized/);
    assert.equal(form.lines.filter(x => x.eventName === 'activated').length, activations);
    assert.equal((await form.command('state')).clicks, 2);

    const visible = await native.capture(window, captures);
    await form.command('hide');
    await assert.rejects(lease(() => native.call(window, 'physical', point(visible))), /refuses to bring the window|did not acknowledge activation|does not hit the target window/);
    const hidden = await form.command('state'); assert.equal(hidden.visible, false); assert.equal(hidden.clicks, 2);
    assert.equal((await native.probe()).foreground, before.foreground);
    t.diagnostic(JSON.stringify({ profile, awareness: ready.awareness, dpi: ready.dpi, monitors: ready.screens.length, capturesDecoded: 3, targetClicks: hidden.clicks, overlayClicks: hidden.overlayClicks }));
  });
});
