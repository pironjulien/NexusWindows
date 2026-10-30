// Regles CEF/Steam du vrai helper, sans GUI : identites fournies, choix de motif et trace de transmission.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

const executable = process.env.NEXUS_FOREGROUND_EXE;
const skip = !executable && 'NEXUS_FOREGROUND_EXE absent';
const run = value => JSON.parse(execFileSync(executable, ['--chromium-selftest'], { input: JSON.stringify(value), windowsHide: true }).toString());
const cef = (children = [{ class: 'CefBrowserWindow', pid: 17, root: 42 }], topClass = 'SDL_app') =>
  run({ cef: { pid: 17, root: 42, topClass, children } });
const click = (patterns, role = 'tab', hosted = true) => run({ click: { cef: hosted, role, patterns } });

test('CEF hosted content permits publication but never enables Chrome focus/type/select simulation for SDL', { skip }, () => {
  const result = cef();
  assert.equal(result.content, true); assert.equal(result.cef, true);
  assert.deepEqual([result.focusSimulation, result.typeSimulation, result.selectSimulation], [false, false, false]);
  assert.equal(result.wheelDevtools, true); assert.equal(result.postedHover, true); assert.equal(result.doubleClickInvoke, false);
  const chrome = cef([], 'Chrome_WidgetWin_1');
  assert.deepEqual([chrome.focusSimulation, chrome.typeSimulation, chrome.selectSimulation], [true, true, true]);
  assert.equal(chrome.content, true);
});

test('an SDL name, a similar child name, another PID or another HWND root never prove hosted CEF', { skip }, () => {
  for (const children of [[],
    [{ class: 'CefBrowserWindowExtra', pid: 17, root: 42 }],
    [{ class: 'Chrome_RenderWidgetHostHWND', pid: 17, root: 42 }],
    [{ class: 'CefBrowserWindow', pid: 18, root: 42 }],
    [{ class: 'CefBrowserWindow', pid: 17, root: 43 }],
  ]) {
    const result = cef(children);
    assert.equal(result.content, false); assert.equal(result.cef, false);
    assert.deepEqual([result.focusSimulation, result.typeSimulation, result.selectSimulation], [false, false, false]);
    assert.equal(result.wheelDevtools, false);
  }
});

test('CEF tabs/lists/radios invoke once, never silently Select; absent Invoke permits one default action', { skip }, () => {
  for (const role of ['tab', 'listitem', 'radio']) {
    const invoked = click({ invoke: 'sent', default: 'applied', select: 'applied' }, role);
    assert.deepEqual(invoked.order, ['invoke', 'default']);
    assert.deepEqual(invoked.calls, ['invoke']); assert.equal(invoked.sent, 1);
    const legacy = click({ invoke: 'missing', default: 'sent', select: 'applied' }, role);
    assert.deepEqual(legacy.calls, ['invoke', 'default']); assert.equal(legacy.sent, 1);
  }
  const native = click({ select: 'applied', invoke: 'sent' }, 'tab', false);
  assert.deepEqual(native.calls, ['select']); assert.equal(native.effect, 'applied');
});

test('CEF sent, unchanged and lost outcomes are terminal; no default action or Selection replays them', { skip }, () => {
  for (const effect of ['sent', 'none']) {
    const result = click({ invoke: effect, default: 'applied', select: 'applied' });
    assert.deepEqual(result.calls, ['invoke']); assert.equal(result.sent, 1);
    assert.equal(result.effect, effect); assert.equal(result.beforeInput, false);
  }
  const lost = click({ invoke: 'lost', default: 'applied', select: 'applied' });
  assert.deepEqual(lost.calls, ['invoke']); assert.equal(lost.sent, 1);
  assert.equal(lost.error, 'COMException'); assert.equal(lost.beforeInput, false);
  const toggle = click({ toggle: 'none', invoke: 'applied', default: 'applied' }, 'checkbox');
  assert.deepEqual(toggle.calls, ['toggle']); assert.equal(toggle.sent, 1); assert.equal(toggle.beforeInput, false);
});

test('CEF without a click pattern refuses before input, allowing only the existing explicit physical path', { skip }, () => {
  const missing = click({ select: 'applied' });
  assert.deepEqual(missing.calls, ['invoke', 'default']); assert.equal(missing.sent, 0);
  assert.equal(missing.error, 'BackgroundNotApplicableException'); assert.equal(missing.beforeInput, true);
  const unreadable = click({ invoke: 'read-error', default: 'applied' });
  assert.deepEqual(unreadable.calls, ['invoke']); assert.equal(unreadable.sent, 0); assert.equal(unreadable.beforeInput, true);
});
