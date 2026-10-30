// DOM scripts the engine injects into Claude Desktop (Chromium) pages, executed with DOM doubles in node:vm. Started by
// ChromiumTests.cs (xUnit), which writes the engine's scripts to NEXUS_CLAUDE_DOM_SCRIPTS first.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const scripts = JSON.parse(readFileSync(process.env.NEXUS_CLAUDE_DOM_SCRIPTS, 'utf8'));

test('production DOM scripts: inner input wins over rich host, open shadow focus is deep, passwords are untouched', () => {
  let hit;
  const document = { activeElement: null, elementFromPoint: () => hit, getSelection: () => ({ anchorNode: hit }) };
  const node = (tagName, parentElement = null, editable = false) => ({ tagName, parentElement, isContentEditable: editable,
    focus() { document.activeElement = this; }, getRootNode: () => document,
    contains(other) { for (let n = other; n; n = n.parentElement) if (n === this) return true; return false; }, matches: () => false });
  const rich = node('DIV', null, true), child = node('SPAN', rich, true);
  hit = child; assert.equal(vm.runInNewContext(scripts.focusEditable, { document }), rich);
  const input = node('INPUT', rich); hit = input;
  assert.equal(vm.runInNewContext(scripts.focusEditable, { document }), input);
  const host = node('CUSTOM'); const shadow = node('INPUT');
  host.shadowRoot = { elementFromPoint: () => shadow, activeElement: null };
  shadow.focus = () => { document.activeElement = host; host.shadowRoot.activeElement = shadow; };
  hit = host; assert.equal(vm.runInNewContext(scripts.focusEditable, { document }), shadow);
  const password = node('INPUT'); password.type = 'password'; hit = password; document.activeElement = input;
  assert.equal(vm.runInNewContext(scripts.focusEditable, { document }), null); assert.equal(document.activeElement, input);
});

test('production select mutation: exact single option and both events, duplicated/disabled choices send no event', () => {
  const { select } = scripts;
  const apply = options => {
    const events = [];
    const element = { tagName: 'SELECT', isConnected: true, disabled: false, value: 'old', options, dispatchEvent: e => events.push(e.type) };
    const fn = vm.runInNewContext(`(function(){${select}})`, { Event: class { constructor(type) { this.type = type; } } });
    return { ok: fn.call(element), value: element.value, events };
  };
  assert.deepEqual(apply([{ text: 'Bleu ciel', value: 'sky' }, { text: ' BLEU ', value: 'blue' }]), { ok: true, value: 'blue', events: ['input', 'change'] });
  for (const options of [[{ text: 'Bleu ciel', value: 'sky' }], [{ text: 'Bleu', value: 'a' }, { text: 'bleu', value: 'b' }],
    [{ text: 'Bleu', value: 'a', disabled: true }], [{ text: 'Bleu', value: 'a', parentElement: { disabled: true } }]]) {
    assert.deepEqual(apply(options), { ok: false, value: 'old', events: [] });
  }
});
