import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable;
function run(simulatedText) {
  return JSON.parse(execFileSync(executable, ['--chromium-selftest'], {
    input: JSON.stringify({ simulatedText }), encoding: 'utf8', windowsHide: true, timeout: 15000,
  }));
}

test('native Claude simulated typing: historical exact prefix and rich-paragraph oracle', { skip }, () => {
  const fixtures = [
    ['', 'abc', '', false, 0], ['ab', 'abc', '', false, 2], ['abc', 'abc', '', false, 3],
    ['abx', 'abc', '', false, null], ['abcabc', 'abc', '', false, null], ['ab\r\ncd', 'ab\ncd', '', false, 5],
    ['\n', 'Bonjour', '', false, 0], ['Bonjour Julien\n', 'Bonjour Julien\nVoici', '', false, 15],
    ['ab\n\n', 'ab\ncd', '', false, 3], ['Bonjour', 'Bonjour', 'Bonjour', true, 0],
    ['BonjourBon', 'Bonjour', 'Bonjour', true, 3], ['Bonjour Julien', 'Bonjour Julien', '\n', true, 14],
    ['Note\nSuite', 'Suite', 'Note\n\n', true, 5], ['xNote', 'x', 'Note', true, null],
    ['A\nB\n', 'A\n\nB', '', false, 4], ['abc ', 'abc', '', false, null], ['ABC', 'abc', '', false, null],
  ];
  for (const [value, text, before, append, expected] of fixtures)
    assert.equal(run({ prefix: { value, text, before, append } }).confirmed, expected, JSON.stringify({ value, text, before, append }));
});

test('native Claude simulated typing: Unicode packets preserve UTF-16 pairs and isolate each real Enter', { skip }, () => {
  const text = '1234567😀\r\nété\n\n終わり\rfin';
  const units = run({ units: text });
  assert.equal(units.join(''), text.replace(/\r\n?/g, '\n'));
  assert.equal(units.filter(x => x === '\n').length, 4);
  for (const unit of units) {
    assert.ok(unit === '\n' || !unit.includes('\n'));
    assert.ok(unit.length <= 8);
    assert.ok(!/[\uD800-\uDBFF]$/.test(unit), 'packet ends inside surrogate pair');
    assert.ok(!/^[\uDC00-\uDFFF]/.test(unit), 'packet starts inside surrogate pair');
  }
});

test('native Claude simulated typing: replacement is erased/read back before characters; append keeps original', { skip }, () => {
  const text = 'ligne1\nligne2 😀';
  const replaced = run({ before: 'old text', text, replace: true });
  assert.equal(replaced.ok, true, JSON.stringify(replaced)); assert.equal(replaced.value, text);
  assert.deepEqual(replaced.events.slice(0, 2), ['select-all', 'delete']);
  assert.equal(replaced.events.filter(x => x === 'delete').length, 1);
  assert.equal(replaced.events.filter(x => x === 'enter').length, 1);
  const appended = run({ before: 'préfixe ', text, replace: false });
  assert.equal(appended.ok, true, JSON.stringify(appended)); assert.equal(appended.value, 'préfixe ' + text);
  assert.equal(appended.events[0], 'caret-end'); assert.ok(!appended.events.includes('delete'));
  const cleared = run({ before: 'erase', text: '', replace: true });
  assert.equal(cleared.ok, true); assert.equal(cleared.value, ''); assert.deepEqual(cleared.events, ['select-all', 'delete']);
});

test('native Claude simulated typing: unread deletion or character acknowledgement stops without replay', { skip }, () => {
  const clear = run({ before: 'old text', text: 'replacement', replace: true, noAck: true });
  assert.equal(clear.ok, false); assert.equal(clear.error, 'AfterInputException');
  assert.deepEqual(clear.events, ['select-all', 'delete']); assert.equal(clear.value, 'old text');
  const chars = run({ before: '', text: '12345678remaining', replace: false, noAck: true });
  assert.equal(chars.ok, false); assert.match(chars.message, /unknown, do not replay/);
  assert.deepEqual(chars.events, ['caret-end', 'chars:12345678']); assert.equal(chars.inputPackets, 1);
});

test('native Claude simulated typing: missing Enter and changed focus never emit the following packet', { skip }, () => {
  const enter = run({ before: '', text: 'A\nDO NOT SEND', replace: false, suppressEnter: true });
  assert.equal(enter.ok, false); assert.deepEqual(enter.events, ['caret-end', 'chars:A', 'enter']);
  const focus = run({ before: '', text: '12345678DO NOT SEND', replace: false, loseAfter: 1 });
  assert.equal(focus.ok, false); assert.deepEqual(focus.events, ['caret-end', 'chars:12345678']);
  assert.equal(focus.value, '12345678');
  const before = run({ before: '', text: 'nothing', replace: false, loseAfter: 0 });
  assert.equal(before.ok, false); assert.deepEqual(before.events, []); assert.equal(before.inputPackets, 0);
});

test('native Claude simulated typing: a send control appearing after A refuses the next real Enter without replay', { skip }, () => {
  const result = run({ before: '', text: 'A\nMUST NOT SEND', replace: false, revealSendAfter: 'A' });
  assert.equal(result.ok, false); assert.equal(result.error, 'AfterInputException');
  assert.match(result.message, /unknown, do not replay/);
  assert.equal(result.value, 'A'); assert.equal(result.inputPackets, 1);
  assert.deepEqual(result.events, ['caret-end', 'chars:A', 'enter-refused:outward-context-changed']);
  assert.ok(!result.events.includes('enter'));
});
