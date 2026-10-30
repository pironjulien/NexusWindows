import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import vm from 'node:vm';

const exe = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !exe;
const run = input => JSON.parse(execFileSync(exe, ['--claude-cdp-selftest'], {
  input: JSON.stringify(input), windowsHide: true, timeout: 15000,
}));

test('Claude DOM keys: F1–F24, page modifiers, printable characters and whole malformed combinations', { skip }, () => {
  const keys = ['ctrl+k', 'alt+f24', 'win+shift+k', 'enter', '+', 'ctrl++', 'ctrl', 'é', 'ctrl+', 'ctrl+shift+', 'f25', 'bogus+x'];
  const r = run(keys);
  assert.deepEqual(r[0], { key:'k', code:'KeyK', vk:75, modifiers:2, text:null });
  assert.equal(r[1].key, 'F24'); assert.equal(r[1].modifiers, 1); assert.equal(r[1].vk, 135);
  assert.equal(r[2].modifiers, 12); assert.equal(r[2].key, 'K');
  assert.equal(r[3].text, '\r'); assert.equal(r[4].text, '+'); assert.equal(r[5].modifiers, 2);
  assert.equal(r[6].key, 'Control'); assert.equal(r[7].text, 'é');
  for (const result of r.slice(8)) assert.ok(result.error);
  assert.deepEqual(run(Array.from({length:24}, (_,i) => `f${i+1}`)).map(r => r.vk), Array.from({length:24}, (_,i) => i+112));
});

test('real CDP socket: one grant, lost acknowledgement never replays, changed focus releases only once', { skip }, () => {
  const normal = run({scenario:'normal'});
  assert.equal(normal.started, 1); assert.equal(normal.outcome, 'acknowledged');
  assert.deepEqual(normal.received, ['rawKeyDown','char','keyUp']);
  const lost = run({scenario:'ack-lost'});
  assert.equal(lost.outcome, 'unknown'); assert.equal(lost.started, 1); assert.deepEqual(lost.received, ['rawKeyDown']);
  assert.equal(lost.cleanups, 1); assert.equal(lost.automaticReplay, false);
  const moved = run({scenario:'focus-after-down'});
  assert.equal(moved.outcome, 'unknown'); assert.equal(moved.started, 1); assert.equal(moved.cleanups, 1);
  assert.deepEqual(moved.received, ['rawKeyDown','keyUp']);
  const account = run({scenario:'account-after-down'});
  assert.equal(account.outcome, 'unknown'); assert.deepEqual(account.received, ['rawKeyDown']);
  for (const scenario of ['account-before','grant-refused']) {
    const r = run({scenario}); assert.equal(r.outcome, 'refused-before-input'); assert.deepEqual(r.received, []);
  }
});

test('production DOM scripts: inner input wins over rich host, open shadow focus is deep, passwords are untouched', { skip }, () => {
  const scripts = run({scripts:true});
  let hit;
  const document = { activeElement:null, elementFromPoint:() => hit, getSelection:() => ({anchorNode:hit}) };
  const node = (tagName, parentElement = null, editable = false) => ({ tagName, parentElement, isContentEditable:editable,
    focus(){document.activeElement=this;}, getRootNode:()=>document,
    contains(other){ for (let n=other;n;n=n.parentElement) if(n===this)return true;return false; }, matches:()=>false });
  const rich = node('DIV',null,true), child = node('SPAN',rich,true);
  hit = child; assert.equal(vm.runInNewContext(scripts.focusEditable,{document}), rich);
  const input = node('INPUT',rich); hit = input;
  assert.equal(vm.runInNewContext(scripts.focusEditable,{document}), input);
  const host = node('CUSTOM'); const shadow = node('INPUT');
  host.shadowRoot = { elementFromPoint:()=>shadow, activeElement:null };
  shadow.focus = () => {document.activeElement=host;host.shadowRoot.activeElement=shadow;};
  hit = host; assert.equal(vm.runInNewContext(scripts.focusEditable,{document}), shadow);
  const password = node('INPUT'); password.type='password'; hit=password; document.activeElement=input;
  assert.equal(vm.runInNewContext(scripts.focusEditable,{document}), null); assert.equal(document.activeElement,input);
});

test('production select mutation: exact single option and both events, duplicated/disabled choices send no event', { skip }, () => {
  const {select} = run({scripts:true});
  const apply = options => {
    const events = [];
    const element = {tagName:'SELECT',isConnected:true,disabled:false,value:'old',options,dispatchEvent:e=>events.push(e.type)};
    const fn = vm.runInNewContext(`(function(){${select}})`,{Event:class {constructor(type){this.type=type;}}});
    return {ok:fn.call(element),value:element.value,events};
  };
  assert.deepEqual(apply([{text:'Bleu ciel',value:'sky'},{text:' BLEU ',value:'blue'}]),{ok:true,value:'blue',events:['input','change']});
  for (const options of [[{text:'Bleu ciel',value:'sky'}], [{text:'Bleu',value:'a'},{text:'bleu',value:'b'}], [{text:'Bleu',value:'a',disabled:true}], [{text:'Bleu',value:'a',parentElement:{disabled:true}}]]) {
    assert.deepEqual(apply(options),{ok:false,value:'old',events:[]});
  }
});

test('multiview: exact document rectangle and CSS/DPR identify one surface, ambiguity never picks by size alone', {skip}, () => {
  const valid = {surfaces:[[100,100,1700,1100],[300,300,700,600]],bounds:[300,300,400,300],inner:[320,240],ratio:1.25};
  assert.equal(run(valid).index,1);
  assert.ok(run({...valid,bounds:[100,100,1600,1000]}).error);
  assert.ok(run({...valid,surfaces:[...valid.surfaces,valid.surfaces[1]]}).error);
  assert.ok(run({...valid,bounds:[301,301,403,300]}).error);
  assert.ok(run({...valid,inner:[321,240]}).index===1);
  assert.ok(run({...valid,inner:[400,240]}).error);
});
