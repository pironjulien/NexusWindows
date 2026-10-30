// Explicit local OCR acceptance: only text painted into owned, otherwise unnamed canvas controls.
// One requested BRAVO click; a changed ALPHA image must subsequently refuse before another click.
import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn,execFileSync} from 'node:child_process';
import {mkdir,writeFile,readFile} from 'node:fs/promises';
import {createHash,randomUUID} from 'node:crypto';
import readline from 'node:readline';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath} from 'node:url';
import {physicalDesktopProbe, assertCursorKept} from './fixtures/physical-desktop-probe.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const executable=process.env.NEXUS_NATIVE_TEST_EXE;
const skip=process.platform!=='win32'||!executable||process.env.NEXUS_NATIVE_OCR_TEST!=='1'
  ? 'Set NEXUS_NATIVE_TEST_EXE and NEXUS_NATIVE_OCR_TEST=1 when the parent owns the desktop slot' : false;
const agents=(process.env.NEXUS_NATIVE_OCR_AGENTS??'claude,codex,antigravity').split(',');
for(const agent of agents) assert.ok(['claude','codex','antigravity'].includes(agent),'Unknown OCR test profile');

function ownedProcess(command,args,env,signal){
  const child=spawn(command,args,{windowsHide:true,stdio:['pipe','pipe','pipe'],env});
  const lines=[],pending=[];let ended,stderr='';
  const fail=(error,terminal=true)=>{if(terminal)ended=error;for(const waiter of pending.splice(0)){clearTimeout(waiter.timer);waiter.reject(error);}};
  const abort=()=>fail(new Error('OCR acceptance aborted'),false);
  signal.addEventListener('abort',abort,{once:true});
  const closed=new Promise(resolve=>child.once('close',(code,signalCode)=>{fail(new Error('Owned OCR process exited'));signal.removeEventListener('abort',abort);resolve({code,signal:signalCode});}));
  child.on('error',fail);child.stdin.on('error',fail);child.stderr.on('data',data=>stderr+=data);
  readline.createInterface({input:child.stdout}).on('line',line=>{
    let value;try{value=JSON.parse(line);}catch{return;}lines.push(value);
    for(const waiter of [...pending])if(waiter.match(value)){clearTimeout(waiter.timer);pending.splice(pending.indexOf(waiter),1);waiter.resolve(value);}
  });
  return{child,lines,stderr:()=>stderr,wait(match,timeout=20000,cleanup=false){
    if(signal.aborted&&!cleanup)return Promise.reject(new Error('OCR acceptance aborted'));
    const old=lines.find(match);if(old)return Promise.resolve(old);if(ended)return Promise.reject(ended);
    return new Promise((resolve,reject)=>{const waiter={match,resolve,reject,timer:setTimeout(()=>{pending.splice(pending.indexOf(waiter),1);reject(new Error('OCR fixture response timeout'));},timeout)};pending.push(waiter);});
  },end(){if(child.stdin.writable)child.stdin.end();},async waitClosed(timeout){
    let timer;
    try{return await Promise.race([closed,new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('Cooperative process exit unproved; no force termination attempted')),timeout);})]);}
    finally{clearTimeout(timer);}
  }};
}

function sameProcess(expected,actual){return !!actual&&expected.pid===actual.pid&&expected.birth===actual.birth;}
function relatedProcesses(rows,known){
  const selected=new Map();const queue=[...known];
  for(let index=0;index<queue.length;index++){
    const parent=queue[index],current=rows.find(row=>row.pid===parent.pid);
    if(sameProcess(parent,current))selected.set(`${current.pid}@${current.birth}`,current);
    if(current&&!sameProcess(parent,current))continue; // A reused parent PID grants no ownership.
    for(const row of rows)if(row.ppid===parent.pid&&Date.parse(row.birth)>=Date.parse(parent.birth)){
      const key=`${row.pid}@${row.birth}`;if(!selected.has(key)){selected.set(key,row);queue.push(row);}
    }
  }
  return [...selected.values()];
}
function processSnapshot(){
  // PID, parent PID and creation time only; no command lines, titles or application content.
  const script="@(Get-CimInstance Win32_Process | Where-Object CreationDate -ne $null | ForEach-Object { [pscustomobject]@{pid=[int]$_.ProcessId;ppid=[int]$_.ParentProcessId;birth=$_.CreationDate.ToUniversalTime().ToString('O')} }) | ConvertTo-Json -Compress";
  return JSON.parse(execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,timeout:10000,encoding:'utf8'}));
}
let retainedSlot=false;

test('OCR lifecycle pure: cancellation rejects RPC waits, then EOF permits cooperative exit',async()=>{
  const controller=new AbortController();const owned=ownedProcess(process.execPath,['-e',"process.stdin.resume();process.stdin.on('end',()=>setTimeout(()=>process.exit(0),16));"],process.env,controller.signal);
  try{
    const pending=owned.wait(()=>false);controller.abort();await assert.rejects(pending,/aborted/);
    assert.equal(owned.child.exitCode,null,'Cancellation must not kill a process that still has finally work');
    owned.end();assert.deepEqual(await owned.waitClosed(1618),{code:0,signal:null});
  }finally{owned.end();await owned.waitClosed(1618);}
});
test('OCR lifecycle pure: PID/birth reuse is foreign and live orphans prevent a clean exit claim',()=>{
  const parent={pid:10,ppid:1,birth:'2026-10-30T00:00:00Z'},worker={pid:11,ppid:10,birth:'2026-10-30T00:00:01Z'};
  assert.deepEqual(relatedProcesses([parent,worker],[parent]),[parent,worker]);
  assert.deepEqual(relatedProcesses([worker],[parent]),[worker],'A child remaining after its parent exits is still unproved cleanup');
  assert.deepEqual(relatedProcesses([{...parent,birth:'2026-10-30T01:00:00Z'},{...worker,birth:'2026-10-30T01:00:01Z'}],[parent]),[]);
  assert.deepEqual(relatedProcesses([{...parent,birth:'2026-10-30T01:00:00Z'},worker],[parent,worker]),[worker],'A previously attested orphan remains owned even when its parent PID was reused');
  assert.equal(sameProcess(worker,{...worker,birth:'2026-10-30T01:00:01Z'}),false);
});

test('OCR lifecycle live: showing and closing the owned fixture alone preserves the desktop',{skip,timeout:15000},async t=>{
  const state=path.join(os.homedir(),'.nexus','nexus-windows','qualification',`ocr-close-only-${randomUUID()}`);
  await mkdir(state,{recursive:true});const control=path.join(state,'fixture-control.txt');
  const report={before:physicalDesktopProbe(),inputSent:false};
  const fixture=ownedProcess('powershell.exe',['-NoProfile','-STA','-File',path.join(root,'codex','tests','native-ocr-form.ps1'),'-ControlFile',control],process.env,t.signal);
  try {
    report.ready=await fixture.wait(value=>value.ready);report.shown=physicalDesktopProbe();
  } finally {
    await writeFile(control,'close:1');report.exit=await fixture.waitClosed(3000);report.after=physicalDesktopProbe();
    await writeFile(path.join(state,'receipt.json'),JSON.stringify(report,null,2));
    t.diagnostic(JSON.stringify({receipt:path.join(state,'receipt.json'),inputSent:false}));
  }
  assert.deepEqual(report.exit,{code:0,signal:null});
  assert.equal(report.after.foreground,report.before.foreground);assertCursorKept(report.before,report.after);
});

for(const agent of agents)test(`native ${agent}: real OCR index hits image-only BRAVO and stale OCR sends zero clicks`,{skip,timeout:60000},async t=>{
  if(retainedSlot){t.skip('Prior OCR cleanup is unproved; no new GUI fixture may start before the parent reviews its receipt');return;}
  const state=path.join(os.homedir(),'.nexus','nexus-windows','qualification',`ocr-${agent}-${randomUUID()}`);
  await mkdir(state,{recursive:true});const control=path.join(state,'fixture-control.txt');
  const probe=physicalDesktopProbe;
  const baseline=probe();
  const fixture=ownedProcess('powershell.exe',['-NoProfile','-STA','-File',path.join(root,'codex','tests','native-ocr-form.ps1'),'-ControlFile',control],process.env,t.signal);
  const server=ownedProcess(executable,['--mcp','--agent',agent],{...process.env,NEXUS_WINDOWS_HOME:state,NEXUS_SHARED_HOME:state,NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(state,'journal'),NEXUS_GARDE_FOUS:path.join(root,'commun','garde-fous.json')},t.signal);
  const receipt={agent,passed:false,primaryCalls:0,staleCalls:0,automaticReplay:false,progress:[],baseline};let id=0,sequence=0,hwnd,cleanupPromise,activeRpc;
  let roots=[],known=[],restoreExpected=baseline;
  const started=performance.now();const record=(event,data={})=>receipt.progress.push({ms:Math.round(performance.now()-started),event,...data});
  const cleanup=()=>cleanupPromise??=(async()=>{
    record('cleanup-start');receipt.closed=false;receipt.aborted=t.signal.aborted;
    const save=()=>{receipt.hits=fixture.lines.filter(value=>value.event==='hit');return writeFile(path.join(state,'receipt.json'),JSON.stringify(receipt,null,2));};
    const firstSave=save().catch(()=>{});let cleanupError;
    try{
      let nativeKnown=[];
      try{
        if(t.signal.aborted||activeRpc!==undefined){
          // Isolated NEXUS_SHARED_HOME only. Native checks STOP and runs its own restoration finally.
          await writeFile(path.join(state,'stop'),JSON.stringify({by:'owned-ocr-acceptance',nonce:randomUUID()}));receipt.stopRequested=true;
        }
        const beforeExit=processSnapshot();
        nativeKnown=relatedProcesses(beforeExit,roots.filter(row=>row.pid===server.child.pid));
        known=relatedProcesses(beforeExit,[...roots,...known]);receipt.ownedProcesses=known;
      }finally{server.end();} // EOF follows the current RPC, even if evidence or identity reads failed.
      receipt.serverExit=await server.waitClosed(10000);assert.deepEqual(receipt.serverExit,{code:0,signal:null});
      assert.equal(roots.length,2,'Both original PID/birth identities must be known before cleanup can be claimed');
      const afterServer=processSnapshot(),remainingServer=relatedProcesses(afterServer,[...roots.filter(row=>row.pid===server.child.pid),...nativeKnown]);
      receipt.remainingServer=remainingServer;assert.deepEqual(remainingServer,[],'Native helper descendants must actually exit');
      receipt.restoreExpected=restoreExpected;receipt.afterNative=probe();
      let restorationError;
      try{assertRestored(restoreExpected,receipt.afterNative);receipt.restored=true;}
      catch(error){restorationError=error;receipt.restored=false;}
      // The native actor is proved stopped. Closing our own fixture is safe even when restoration failed;
      // keep that failure terminal, but do not leave a test window orphaned merely because its probe failed.
      if(fixture.child.exitCode==null){await command('state',true).then(value=>{receipt.cleanupCounters=value;});await writeFile(control,`close:${++sequence}`);}
      receipt.fixtureExit=await fixture.waitClosed(3000);assert.deepEqual(receipt.fixtureExit,{code:0,signal:null});
      receipt.remainingOwned=relatedProcesses(processSnapshot(),[...roots,...known]);assert.deepEqual(receipt.remainingOwned,[],'Every owned PID/birth and descendant must be gone');
      receipt.afterCleanup=probe();assertRestored(restoreExpected,receipt.afterCleanup);
      if(restorationError)throw restorationError;
      receipt.closed=true;
    }catch(error){cleanupError=error;retainedSlot=true;receipt.cleanupFailure={type:error.name,message:error.message.slice(0,200)};receipt.slotRetained=true;}
    await firstSave;receipt.aborted=t.signal.aborted;receipt.passed=!!receipt.gesturesPassed&&receipt.closed&&!receipt.aborted;record('cleanup',{closed:receipt.closed,restored:receipt.restored??false});await save();
    t.diagnostic(JSON.stringify({receipt:path.join(state,'receipt.json'),passed:receipt.passed,closed:receipt.closed,slotRetained:!!receipt.slotRetained,primaryCalls:receipt.primaryCalls,staleCalls:receipt.staleCalls}));
    if(cleanupError)throw cleanupError;
  })();
  const aborted=()=>{receipt.aborted=true;record('aborted');void cleanup().catch(()=>{});};t.signal.addEventListener('abort',aborted,{once:true});t.after(cleanup);
  async function call(name,args){
    t.signal.throwIfAborted();const current=++id,pending=server.wait(value=>value.id===current);record('rpc-start',{id:current,tool:name});
    server.child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method:'tools/call',params:{name,arguments:args}})+'\n');
    activeRpc=current;const reply=await pending;activeRpc=undefined;t.signal.throwIfAborted();assert.ok(!reply.error,'OCR MCP transport failed');
    record('rpc-end',{id:current,isError:!!reply.result.isError,status:reply.result.structuredContent?.status});return reply.result;
  }
  async function command(action,cleanup=false){const current=++sequence;await writeFile(control,`${action}:${current}`);return fixture.wait(value=>value.sequence===current,3000,cleanup);}
  async function observe(){const result=await call('look',{window:hwnd,show:'both',marks:'none'});assert.ok(!result.isError,'The real OCR look failed');return result.structuredContent;}
  function assertRestored(before,after){assert.equal(after.foreground,before.foreground,'The native foreground must be restored');assertCursorKept(before,after,'The native cursor must be unchanged or restored');}
  function exactOcr(view,name){
    const native=view.elements.filter(element=>!element.ocr);
    assert.ok(native.length>=3&&native.length<6,`Thin canvas tree required; actual native node count ${native.length}`);
    assert.ok(!native.some(element=>element.role!=='window'&&/\b(ALPHA|BRAVO|CHARLIE|DELTA)\b/.test(element.name??'')),'Painted labels must have no UIA text node');
    const found=view.elements.filter(element=>element.ocr===true&&element.name===name);assert.equal(found.length,1,`Real OCR must uniquely recognize ${name}`);
    const target=found[0];assert.deepEqual(target.ocrFrame,view.frame,'OCR coordinates must be bound to the observed native frame');
    assert.equal(target.bounds.length,4);assert.ok(target.bounds[2]>0&&target.bounds[3]>0);assert.ok(!target.runtimeId,'OCR must not fabricate a UIA runtime identity');
    assert.deepEqual(view.capture.windowFrame,view.frame);return target;
  }
  try{
    const initial=processSnapshot(),found=[fixture.child.pid,server.child.pid].map(pid=>initial.find(row=>row.pid===pid));
    assert.ok(found.every(row=>row?.birth),'Both owned processes need independently read PID/birth identities');roots=found;known=relatedProcesses(initial,roots);receipt.ownedProcesses=known;
    const ready=await fixture.wait(value=>value.ready);hwnd=ready.hwnd;receipt.fixture={hwnd,pid:ready.pid};
    const view=await observe();const target=exactOcr(view,'BRAVO');
    assert.equal(view.processId,ready.pid);assert.ok(view.processStartedUtc);assert.ok(view.observation&&view.image);
    const capturePath=path.resolve(view.capture.path);assert.ok(capturePath.toLowerCase().startsWith((path.resolve(state)+path.sep).toLowerCase()),'Capture must belong to this synthetic fixture state');
    const bytes=await readFile(capturePath);assert.ok(bytes.length>0);
    receipt.observed={observation:view.observation,image:view.image,pid:view.processId,birth:view.processStartedUtc,frame:view.frame,index:target.index,bounds:target.bounds,captureSha256:createHash('sha256').update(bytes).digest('hex')};
    const before=probe();restoreExpected=before;receipt.primaryCalls++;
    const clicked=await call('act',{window:hwnd,foreground:true,...(agent==='claude'?{then:'none'}:{}),steps:[{do:'click',target:`#${target.index}`,expect:{title:'Native OCR BRAVO 1'}}]});
    const after=probe();receipt.afterPrimary=after;
    receipt.primary={isError:!!clicked.isError,status:clicked.structuredContent?.status,stepStatus:clicked.structuredContent?.steps?.[0]?.status,message:clicked.structuredContent?.steps?.[0]?.message,technique:clicked.structuredContent?.steps?.[0]?.technique,trace:clicked.structuredContent?.steps?.[0]?.trace};
    receipt.counters=await command('state');
    receipt.hits=fixture.lines.filter(value=>value.event==='hit');
    assertRestored(before,after);
    // Any '~' or refusal ends this case here: no second click, fallback replay or guessed target.
    assert.ok(!clicked.isError&&receipt.primary.status==='success',JSON.stringify(receipt.primary));
    await fixture.wait(value=>value.event==='hit'&&value.target==='BRAVO'&&value.total===1);
    assert.deepEqual([receipt.counters.total,receipt.counters.alpha,receipt.counters.bravo,receipt.counters.charlie,receipt.counters.missed],[1,0,1,0,0]);
    const fresh=await observe();const stale=exactOcr(fresh,'ALPHA');assert.equal(fresh.processStartedUtc,view.processStartedUtc);
    await command('mutate');const beforeStale=probe();restoreExpected=beforeStale;receipt.staleCalls++;
    const refused=await call('act',{window:hwnd,foreground:true,...(agent==='claude'?{then:'none'}:{}),steps:[{do:'click',target:`#${stale.index}`,expect:{title:'Native OCR ALPHA 2'}}]});
    assertRestored(beforeStale,probe());
    receipt.stale={isError:!!refused.isError,status:refused.structuredContent?.status,stepStatus:refused.structuredContent?.steps?.[0]?.status};
    const message=refused.isError?refused.content?.find(value=>value.type==='text')?.text:refused.structuredContent?.steps?.[0]?.message;
    assert.notEqual(receipt.stale.status,'success');assert.match(message??'',/OCR.*(moved|changed|ambiguous)/i,'Changed canvas text must be refused by a fresh OCR read');
    const final=receipt.finalCounters=await command('state');
    assert.deepEqual([final.total,final.alpha,final.bravo,final.charlie,final.missed],[1,0,1,0,0],'The stale OCR index must emit zero additional clicks');
    receipt.gesturesPassed=true;record('passed');
  }catch(error){receipt.failure={type:error.name,code:error.code};record('failed',receipt.failure);throw error;}
  finally{try{await cleanup();}finally{t.signal.removeEventListener('abort',aborted);}}
});
