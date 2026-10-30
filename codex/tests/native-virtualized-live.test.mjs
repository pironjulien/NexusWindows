import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const enabled = process.platform === 'win32' && executable && process.env.NEXUS_NATIVE_VIRTUALIZED_TEST === '1';
const skip = enabled ? false : 'Explicit opt-in NEXUS_NATIVE_VIRTUALIZED_TEST=1 and final NEXUS_NATIVE_TEST_EXE required';
const agent = process.env.NEXUS_NATIVE_VIRTUALIZED_AGENT ?? 'claude';
assert.ok(['claude', 'antigravity', 'codex'].includes(agent));

function jsonProcess(command, args, env = process.env) {
  const child = spawn(command, args, { windowsHide: true, env, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = [], pending = []; let stderr = '';
  child.on('error', error => {
    stderr += error.message;
    for (const waiter of pending.splice(0)) { clearTimeout(waiter.timer); waiter.reject(error); }
  });
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
      const waiter = { match, resolve, reject, timer: setTimeout(() => {
        pending.splice(pending.indexOf(waiter), 1); reject(new Error(`WPF virtualization timed out: ${stderr}`));
      }, ms) }; pending.push(waiter);
    });
  } };
}

test(`native ${agent}: a genuinely virtualized WPF item is unique, realized, selected and committed by real handlers`, {
  skip, timeout: 120000,
}, async t => {
  const state = path.join(os.homedir(), '.nexus', 'nexus-windows', 'qualification', `virtualized-${agent}-${randomUUID()}`);
  await mkdir(state, { recursive: true });
  const receipt = { agent, started_at: new Date().toISOString(), executable, completed: false, real_provider_qualified: false };
  const fixture = jsonProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'codex', 'tests', 'native-virtualized-wpf.ps1')]);
  const server = jsonProcess(executable, ['--mcp', '--agent', agent], { ...process.env, NEXUS_WINDOWS_HOME: state,
    NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'),
    NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json') });
  let id = 0, hwnd;
  const call = async (name, args) => {
    const current = ++id, pending = server.wait(value => value.id === current);
    server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
    const result = await pending;
    if (result.error) throw new Error(JSON.stringify(result.error));
    return result.result;
  };
  const successful = result => { assert.ok(!result.isError, JSON.stringify(result)); return result.structuredContent; };
  const look = async () => successful(await call('look', { window: hwnd, show: 'text', max_elements: 500 }));
  const probe = (stage, ready) => JSON.parse(execFileSync('powershell.exe', [
    '-NoProfile', '-MTA', '-File', path.join(root, 'codex', 'tests', 'native-virtualized-probe.ps1'),
    '-Window', String(hwnd), '-TargetName', ready.target_name, '-DuplicateName', ready.duplicate_name, '-Stage', stage,
  ], { encoding: 'utf8', windowsHide: true, timeout: 20000 }).trim());
  try {
    const ready = receipt.fixture_initial = await fixture.wait(value => value.ready);
    hwnd = ready.top;
    assert.equal(ready.count, 3000);
    assert.equal(ready.is_virtualizing, true);
    assert.equal(ready.target_realized, false, 'The target must have no ListBoxItem visual at startup');
    assert.equal(ready.target_data_count, 1);
    assert.equal(ready.duplicate_data_count, 2);
    assert.ok(ready.realized_count > 0 && ready.realized_count < ready.count / 10, 'The fixture must create only a viewport-sized subset');
    const first = receipt.first_native_look = await look();
    assert.ok(!first.elements.some(element => element.name === ready.target_name), 'The real target must be absent from the initial native UIA tree');
    const capabilities = receipt.capabilities_before = probe('Before', ready);
    assert.equal(capabilities.list_framework, 'WPF');
    assert.equal(capabilities.item_container, true, 'Fail if the actual WPF version does not expose ItemContainer');
    assert.equal(capabilities.target_found, true);
    assert.equal(capabilities.target_unique, true);
    assert.equal(capabilities.target_virtualized_item, true, 'A normal offscreen element is insufficient: require the real placeholder pattern');
    assert.equal(capabilities.target_in_tree_before_lookup, false);
    assert.equal(capabilities.target_in_tree_after_lookup, false, 'The read-only probe must not realize the item');
    assert.equal(capabilities.duplicate_count, 2);
    assert.equal(capabilities.duplicate_distinct, true);
    assert.equal(capabilities.mutations_sent, 0);
    assert.equal(fixture.lines.filter(value => value.event === 'target-realized').length, 0);

    // The same real provider contains two data objects with the same name. Refusal must precede realization/selection.
    const ambiguous = receipt.ambiguous = await call('act', { window: hwnd, then: 'none', steps: [{ kind: 'click',
      selector: { role: 'listitem', name: ready.duplicate_name },
      expect: { selector: { role: 'listitem', name: ready.duplicate_name }, field: 'selected', equals: true } }] });
    const ambiguousText = JSON.stringify(ambiguous);
    assert.match(ambiguousText, /several|ambiguous|unique|unambiguous/i, ambiguousText);
    assert.ok(ambiguous.isError || ambiguous.structuredContent?.status === 'failed', ambiguousText);
    assert.equal(fixture.lines.filter(value => value.event === 'selected' || value.event === 'committed' || value.event === 'target-realized').length, 0,
      'Ambiguity must leave the real selection and target visuals untouched');

    const before = await look();
    assert.ok(!before.elements.some(element => element.name === ready.target_name));
    const selected = receipt.selected = successful(await call('act', { window: hwnd, then: 'none', steps: [{ kind: 'click',
      selector: { role: 'listitem', name: ready.target_name },
      expect: { selector: { role: 'listitem', name: ready.target_name }, field: 'selected', equals: true } }] }));
    assert.equal(selected.status, 'success', JSON.stringify(selected));
    assert.equal(selected.steps[0].status, 'verified');
    receipt.oracle_realized = await fixture.wait(value => value.event === 'target-realized');
    const oracle = receipt.oracle_selection = await fixture.wait(value => value.event === 'selected' && value.selected_key === ready.target_key);
    assert.equal(oracle.target_realized, true);
    assert.equal(oracle.target_visible, true);
    assert.equal(oracle.target_in_viewport, true);
    assert.ok(oracle.vertical_offset > ready.vertical_offset, 'The real ScrollViewer must move to the previously unrealized item');
    assert.equal(oracle.commits, 0);
    assert.equal(fixture.lines.filter(value => value.event === 'selected').length, 1, 'The semantic click must not be replayed');
    const after = receipt.capabilities_after = probe('After', ready);
    assert.equal(after.target_in_tree_before_lookup, true);
    assert.equal(after.target_unique, true);
    assert.equal(after.target_selected, true);
    assert.equal(after.target_offscreen, false);
    assert.ok(after.vertical_scroll_percent > 0);

    await look();
    const committed = receipt.committed = successful(await call('act', { window: hwnd, then: 'none', steps: [{ kind: 'click',
      selector: { role: 'button', name: 'Valider la selection' },
      expect: { title: `Native virtualized committed ${ready.target_key} 1` } }] }));
    assert.equal(committed.status, 'success', JSON.stringify(committed));
    const commit = receipt.oracle_commit = await fixture.wait(value => value.event === 'committed');
    assert.equal(commit.selected_key, ready.target_key);
    assert.equal(commit.commits, 1);
    assert.equal(fixture.lines.filter(value => value.event === 'committed').length, 1);
    receipt.completed = true;
    receipt.real_provider_qualified = true;
    t.diagnostic(JSON.stringify({ receipt: path.join(state, 'receipt.json'), capabilities, after,
      oracle, commit, proof: 'Standard WPF peers, independent data selection and Button.Click handlers; no simulated provider' }));
  } catch (error) {
    receipt.failure = error.message;
    throw error;
  } finally {
    if (hwnd) await call('window', { window: hwnd, action: 'close' }).catch(error => { receipt.cleanup_error = error.message; });
    receipt.fixture_events = fixture.lines;
    receipt.finished_at = new Date().toISOString();
    await writeFile(path.join(state, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
    server.child.stdin.end();
    if (fixture.child.exitCode == null) fixture.child.kill();
    if (server.child.exitCode == null) server.child.kill();
    // Retain only the small local receipt and journal. This is test evidence, outside OneDrive, never personal content.
  }
});
