// Real UI oracle: no user application, message or credential is accessed.
import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdir, writeFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import {fileURLToPath} from 'node:url';
import {randomUUID} from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable || process.env.NEXUS_NATIVE_PHYSICAL_TEST !== '1'
  ? 'Parent must provide final native build and opt in to local physical acceptance' : false;

function jsonProcess(exe, args, env = process.env) {
  const child = spawn(exe, args, {windowsHide:true, stdio:['pipe','pipe','pipe'], env});
  const lines = [], pending = []; let stderr = '', ended;
  function fail(error) {
    ended = error;
    for (const waiter of pending.splice(0)) { clearTimeout(waiter.timer); waiter.reject(error); }
  }
  child.on('error', fail);
  child.on('close', (code, signal) => fail(new Error(`Owned test process exited (${code ?? signal}): ${stderr}`)));
  child.stderr.on('data', data => { stderr += data; });
  readline.createInterface({input:child.stdout}).on('line', line => {
    let value; try { value = JSON.parse(line); } catch { return; }
    lines.push(value);
    for (const waiter of [...pending]) if (waiter.match(value)) {
      clearTimeout(waiter.timer); pending.splice(pending.indexOf(waiter),1); waiter.resolve(value);
    }
  });
  return {child, lines, wait(match, timeout=30000) {
    const found = lines.find(match); if (found) return Promise.resolve(found);
    if (ended) return Promise.reject(ended);
    return new Promise((resolve,reject) => {
      const waiter = {match,resolve,reject,timer:setTimeout(() => { pending.splice(pending.indexOf(waiter),1); reject(new Error(`Fixture timeout: ${stderr}`)); },timeout)};
      pending.push(waiter);
    });
  }};
}

for (const agent of ['codex','claude']) test(`native ${agent}: full image and zoom keep exact points; a permuted index sends no click`, {skip,timeout:120000}, async t => {
  const state = path.join(os.homedir(),'.nexus','nexus-windows','qualification',`image-index-${agent}-${randomUUID()}`);
  await mkdir(state,{recursive:true});
  const control = path.join(state,'fixture-control.txt');
  const fixture = jsonProcess('powershell.exe',['-NoProfile','-STA','-File',path.join(root,'codex','tests','native-image-index-form.ps1'),'-ControlFile',control]);
  const server = jsonProcess(executable,['--mcp','--agent',agent],{...process.env,NEXUS_WINDOWS_HOME:state,
    NEXUS_GARDE_FOUS:path.join(root,'commun','garde-fous.json'),NEXUS_SHARED_HOME:state,NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(state,'journal')});
  let id=0,hwnd;
  async function raw(name,args) {
    const current=++id,pending=server.wait(r=>r.id===current);
    server.child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method:'tools/call',params:{name,arguments:args}})+'\n');
    const reply=await pending; assert.ok(!reply.error,JSON.stringify(reply.error)); return reply.result;
  }
  async function call(name,args) { const result=await raw(name,args); assert.ok(!result.isError,JSON.stringify(result)); return result; }
  function imagePoint(element,capture) {
    const [x,y,w,h]=element.bounds;
    return [(x+w/2-capture.frame.left)*capture.width/capture.frame.width,(y+h/2-capture.frame.top)*capture.height/capture.frame.height];
  }
  async function clickPoint(point,target,count) {
    const result=await call('act',{window:hwnd,foreground:true,...(agent==='claude'?{then:'none'}:{}),steps:[{do:'click',target:point,expect:{title:`Native image ${target} ${count}`}}]});
    if (result.structuredContent?.steps?.[0]?.status==='deferred') { t.skip('User-presence guard deferred before any pointer input'); return false; }
    assert.equal(result.structuredContent.status,'success',JSON.stringify(result.structuredContent));
    await fixture.wait(r=>r.event==='hit'&&r.target===target&&r.count===count);
    return true;
  }
  try {
    const ready=await fixture.wait(r=>r.ready); hwnd=ready.top;
    if (ready.width<=2000) { t.skip('This display did not expose the wide fixture required to verify downscaling'); return; }
    const readiness = {window:ready.window,dwm:ready.dwm,observations:[]};
    let stableFrame;
    for (let attempt=0;attempt<3;attempt++) {
      const inspection=(await call('look',{window:hwnd,show:'elements'})).structuredContent;
      readiness.observations.push({frame:inspection.frame,treeMs:inspection.treeMs,elements:inspection.elements.length});
      stableFrame=inspection.frame;
      if (stableFrame&&['left','top','width','height'].every(key=>Number.isFinite(stableFrame[key]))&&stableFrame.width>0&&stableFrame.height>0) break;
      await new Promise(resolve=>setTimeout(resolve,161));
    }
    await writeFile(path.join(state,'readiness.json'),JSON.stringify(readiness,null,2));
    t.diagnostic(JSON.stringify({agent,readiness}));
    assert.ok(stableFrame&&stableFrame.width>0&&stableFrame.height>0,'read-only native/UIA inspection must attest a stable frame before the first image');
    const full=(await call('look',{window:hwnd,show:'image',marks:'none'})).structuredContent;
    assert.ok(full.capture.width<full.capture.frame.width,'the real window must be downscaled in the published image');
    const b=full.elements.find(e=>e.name==='Target B'&&e.role==='button'); assert.ok(b);
    if (!await clickPoint(imagePoint(b,full.capture),'Target B',1)) return;
    const basis=(await call('look',{window:hwnd,show:'image',marks:'none'})).structuredContent;
    const a=basis.elements.find(e=>e.name==='Target A'&&e.role==='button'); assert.ok(a);
    const center=imagePoint(a,basis.capture);
    const box=[Math.max(0,center[0]-80),Math.max(0,center[1]-80),Math.min(basis.capture.width,center[0]+80),Math.min(basis.capture.height,center[1]+80)];
    const zoom=(await call('look',{window:hwnd,show:'image',region:box,...(agent==='codex'?{image:basis.image}:{}),marks:'none'})).structuredContent;
    assert.ok(zoom.capture.frame.width<basis.capture.frame.width,'zoom really captured a region');
    if (!await clickPoint(agent==='claude'?center:imagePoint(a,zoom.capture),'Target A',2)) return;
    assert.deepEqual(fixture.lines.filter(r=>r.event==='hit').map(r=>r.target),['Target B','Target A']);
    const observed=(await call('look',{window:hwnd,show:'elements'})).structuredContent;
    const alpha=observed.elements.find(e=>e.name==='Alpha'&&e.role==='listitem'); assert.ok(alpha);
    await writeFile(control,'swap'); await fixture.wait(r=>r.event==='swapped');
    const selections=fixture.lines.filter(r=>r.event==='selection').length;
    const hits=fixture.lines.filter(r=>r.event==='hit').length;
    const rejected=await raw('act',{window:hwnd,foreground:true,...(agent==='claude'?{then:'none'}:{}),steps:[{do:'click',target:`#${alpha.index}`} ]});
    const serialized=JSON.stringify(rejected);
    assert.match(serialized,/replaced|context|identity|structure changed/i,'a stale index must refuse before any gesture');
    assert.notEqual(rejected.structuredContent?.status,'success');
    await writeFile(control,'state'); const counters=await fixture.wait(r=>r.event==='state');
    assert.equal(counters.selections,selections,'the new occupant of the old index must never receive selection');
    assert.equal(counters.hits,hits,'the stale-index call must emit zero clicks');
    t.diagnostic(JSON.stringify({agent,width:full.capture.frame.width,imageWidth:full.capture.width,targets:['Target B','Target A'],staleIndexSent:0,state}));
  } finally {
    await writeFile(control,'close').catch(()=>{});
    server.child.stdin.end();
    if (fixture.child.exitCode==null) fixture.child.kill();
    if (server.child.exitCode==null) server.child.kill();
    // Retain only this synthetic local evidence; no user content or project copy was created.
  }
});
