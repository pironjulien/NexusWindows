import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

// The parent qualification sets this to its one fresh shared build, never an older installed helper.
const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Set NEXUS_NATIVE_TEST_EXE to the final shared build' : false;

test('native Store support: state readback, identity gate, no retry and hidden application classification', { skip }, () => {
  const result = JSON.parse(execFileSync(executable, ['--store-parity-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(result.ok, true);
  assert.ok(result.checks >= 17);
});

test('native virtualized materialization: Realize precedes scrolling, interruption stops and every pattern is released', { skip }, () => {
  const result = JSON.parse(execFileSync(executable, ['--virtualization-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(result.ok, true);
  assert.ok(result.checks >= 7);
});

function jsonProcess(command, args, env = process.env) {
  const child = spawn(command, args, { windowsHide: true, env, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = [], pending = []; let stderr = '';
  child.stderr.on('data', part => { stderr += part; });
  readline.createInterface({ input: child.stdout }).on('line', line => {
    let value; try { value = JSON.parse(line); } catch { return; }
    lines.push(value);
    for (const waiter of [...pending]) if (waiter.match(value)) {
      clearTimeout(waiter.timer); pending.splice(pending.indexOf(waiter), 1); waiter.resolve(value);
    }
  });
  return { child, lines, wait(match, ms = 20000) {
    const found = lines.find(match); if (found) return Promise.resolve(found);
    return new Promise((resolve, reject) => {
      const waiter = { match, resolve, timer: setTimeout(() => {
        pending.splice(pending.indexOf(waiter), 1); reject(new Error(`Native fixture timed out: ${stderr}`));
      }, ms) }; pending.push(waiter);
    });
  } };
}

for (const agent of ['codex', 'claude', 'antigravity']) {
  test(`native ${agent}: focusing a real Envoyer button never invokes its independent click handler`, {
    skip: skip || process.env.NEXUS_NATIVE_PHYSICAL_TEST !== '1' && 'Parent opt-in NEXUS_NATIVE_PHYSICAL_TEST=1 required', timeout: 45000,
  }, async t => {
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
    const state = path.join(os.homedir(), '.nexus', 'nexus-windows', 'qualification', `focus-${agent}-${randomUUID()}`);
    await mkdir(state, { recursive: true });
    const fixture = jsonProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'codex', 'tests', 'native-safety-form.ps1')]);
    const server = jsonProcess(executable, ['--mcp', '--agent', agent], { ...process.env, NEXUS_WINDOWS_HOME: state,
      NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal') });
    let id = 0, hwnd;
    async function call(name, args) {
      const current = ++id, result = server.wait(r => r.id === current);
      server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
      const response = await result;
      if (response.error) throw new Error(JSON.stringify(response.error));
      return response.result;
    }
    try {
      hwnd = (await fixture.wait(r => r.ready)).top;
      const first = (await call('look', { window: hwnd })).structuredContent;
      const send = first.elements.find(e => e.role === 'button' && e.name === 'Envoyer');
      assert.ok(send); assert.equal(send.focused, false);
      // The interaction keeps the app's focus until the postcondition was read, then gives Julien his foreground back.
      const result = await call('act', { window: hwnd, foreground: true, interaction: true, steps: [{ kind: 'focus',
        selector: { role: 'button', name: 'Envoyer' }, expect: { selector: { role: 'button', name: 'Envoyer' }, field: 'focused', equals: true } }] });
      if (result.isError && /presence|user is typing|Julien|interruption/i.test(result.content?.[0]?.text ?? '')) {
        t.skip('The real user-presence guard deferred before input'); return;
      }
      assert.ok(!result.isError, JSON.stringify(result));
      const data = result.structuredContent;
      assert.equal(data.steps[0].status, 'verified', JSON.stringify(data));
      assert.equal(data.steps[0].technique, 'foreground:uia');
      const after = await call('look', { window: hwnd });
      assert.equal(after.structuredContent.title, 'Native safety fixture');
      assert.equal(fixture.lines.filter(line => /^local send /.test(line.event ?? '')).length, 0,
        'The fixture click counter is an independent oracle; SetFocus must not invoke Envoyer');
    } finally {
      if (hwnd) await call('window', { window: hwnd, action: 'close' }).catch(() => {});
      server.child.stdin.end(); fixture.child.kill(); server.child.kill();
      // Keep the small local journal as qualification evidence; no project copy or personal content is stored here.
    }
  });
}
