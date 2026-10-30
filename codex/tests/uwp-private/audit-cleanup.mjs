// Reconcile an existing proof after a driver assertion failure; never launch or suspend anything.
// Original receipt remains immutable. Qualification uses its captured independent evidence only.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

assert.equal(process.env.NEXUS_UWP_GUI_SLOT,'1','Root exclusive GUI slot is required');
const source=path.dirname(fileURLToPath(import.meta.url)), project=path.resolve(source,'../../..');
const planPath=process.env.NEXUS_UWP_PRIVATE_PLAN, executable=process.env.NEXUS_NATIVE_TEST_EXE;
assert.ok(planPath&&executable);
const plan=JSON.parse(await readFile(planPath,'utf8')), home=path.dirname(planPath);
const originalBytes=await readFile(path.join(home,'lifecycle-receipt.json'));
const original=JSON.parse(originalBytes), nativeHash=createHash('sha256').update(await readFile(executable)).digest('hex');
assert.equal(original.plan,planPath); assert.equal(original.executable,executable); assert.equal(original.native_sha256,nativeHash);
const audit={schema:1,started_at:new Date().toISOString(),source_receipt:path.join(home,'lifecycle-receipt.json'),
  source_sha256:createHash('sha256').update(originalBytes).digest('hex'),native_sha256:nativeHash,
  original_driver_failure:original.failure,engine_lifecycle_qualified:false,slot_released:false,
  no_launch_no_suspend_no_lifecycle_replay:true};
const main=data=>data.processes.filter(item=>path.resolve(item.image).toLowerCase()===path.resolve(plan.executable).toLowerCase());
function stateText(look) {
  const matches=look.elements.filter(element=>element.automationId==='NexusUwpState');
  assert.equal(matches.length,1); const match=/resume=(\d+) \| suspend=(\d+)/.exec(matches[0].name);
  assert.ok(match); return {resume:Number(match[1]),suspend:Number(match[2]),name:matches[0].name};
}
try {
  assert.equal(original.suspended.state,3);
  assert.equal(original.after_native_resume.state,1);
  assert.equal(original.resume_look.storeResume.Requested,true);
  assert.equal(original.resume_look.storeContentReady,true);
  assert.ok(original.resume_look.frame.width>0&&original.resume_look.frame.height>0);
  const before=main(original.suspended), after=main(original.after_native_resume);
  assert.equal(before.length,1); assert.equal(after.length,1); assert.equal(before[0].is_app_container,true); assert.equal(after[0].is_app_container,true);
  assert.equal(before[0].pid,after[0].pid); assert.equal(before[0].creation_filetime,after[0].creation_filetime);
  const initial=stateText(original.initial_look), resumed=stateText(original.resume_look);
  assert.equal(resumed.resume,initial.resume+1); assert.equal(resumed.suspend,initial.suspend+1);
  assert.equal(original.foreground_before_native_resume.foreground,original.foreground_after_native_resume.foreground);
  audit.evidence={suspended_state:3,resumed_state:1,pid:after[0].pid,birth:after[0].creation_filetime,
    app_container:true,initial,resumed,store_resume:original.resume_look.storeResume,frame:original.resume_look.frame,
    foreground:original.foreground_after_native_resume.foreground};
  audit.engine_lifecycle_qualified=true;
} catch(error) { audit.proof_failure=error.message; }

const python=path.join(process.env.LOCALAPPDATA,'Programs/Python/Python314/python.exe');
const status=()=>JSON.parse(execFileSync(python,['-I','-B',path.join(source,'probe.py'),'--plan',planPath],{windowsHide:true,encoding:'utf8',timeout:20000}));
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const env={...process.env,NEXUS_GARDE_FOUS:path.join(project,'commun/garde-fous.json'),NEXUS_WINDOWS_HOME:path.join(home,'mcp-state'),NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(home,'mcp-state/journal')};
delete env.NEXUS_SHARED_HOME;
const server=spawn(executable,['--mcp','--agent','claude'],{windowsHide:true,stdio:['pipe','pipe','pipe'],env});
const pending=new Map(); let id=0, stderr='';
server.stderr.on('data',chunk=>{stderr+=chunk;});
readline.createInterface({input:server.stdout}).on('line',line=>{
  let response; try { response=JSON.parse(line); } catch { return; }
  const waiter=pending.get(response.id); if(waiter){pending.delete(response.id);clearTimeout(waiter.timer);waiter.resolve(response);}
});
async function call(name,args) {
  const current=++id;
  const response=new Promise((resolve,reject)=>pending.set(current,{resolve,reject,timer:setTimeout(()=>{pending.delete(current);reject(new Error('MCP cleanup timeout: '+stderr));},30000)}));
  server.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method:'tools/call',params:{name,arguments:args}})+'\n');
  const result=await response; if(result.error||result.result?.isError) throw new Error(JSON.stringify(result));
  return result.result.structuredContent??result.result;
}
try {
  let actual=audit.before_cleanup=status();
  if(actual.registered&&actual.processes.length) {
    const owned=main(actual); assert.equal(owned.length,1); assert.equal(owned[0].is_app_container,true);
    assert.equal(owned[0].pid,main(original.after_native_resume)[0].pid);
    assert.equal(owned[0].creation_filetime,main(original.after_native_resume)[0].creation_filetime);
    const hwnd=original.resume_look.hwnd;
    assert.ok(actual.windows.some(window=>window.hwnd===hwnd&&window.pid===owned[0].pid));
    audit.cleanup_look=await call('look',{window:hwnd,show:'text'});
    assert.equal(audit.cleanup_look.contentProcessId,owned[0].pid);
    const close=audit.cleanup_look.elements.filter(element=>element.automationId==='NexusUwpClose'&&element.role==='button'&&element.name==='Close private fixture');
    assert.equal(close.length,1);
    // The previous driver's recorded already-satisfied result proves it sent no input.
    assert.equal(original.cleanup_close?.steps?.[0]?.status,'already-satisfied');
    audit.cleanup_close=await call('act',{window:hwnd,then:'none',steps:[{kind:'click',
      selector:{role:'button',automationId:'NexusUwpClose',name:'Close private fixture'},
      expect:{selector:{role:'text',automationId:'NexusUwpState'},exists:false},timeoutMs:1000}]});
    for(let attempt=0;attempt<20;attempt++){actual=status();if(actual.processes.length===0)break;await pause(161.8);}
  }
  audit.after_close=actual;
  assert.equal(actual.processes.length,0,'Private package still has a live process; no forced termination or unregister');
  if(actual.registered) audit.removed=JSON.parse(execFileSync('pwsh.exe',['-NoProfile','-NonInteractive','-File',path.join(source,'register.ps1'),'-Plan',planPath,'-Remove','-Apply'],{windowsHide:true,encoding:'utf8',timeout:20000}));
  audit.after_remove=status(); assert.equal(audit.after_remove.registered,false);
  audit.slot_released=true;
} catch(error) { audit.cleanup_failure=error.message; }
finally {
  server.stdin.end();await pause(150);if(server.exitCode===null)server.kill();
  audit.stderr=stderr; audit.finished_at=new Date().toISOString();
  const output=path.join(home,'lifecycle-proof-audit.json'); await writeFile(output,JSON.stringify(audit,null,2)+'\n');
  console.log(JSON.stringify({receipt:output,qualified:audit.engine_lifecycle_qualified,slot_released:audit.slot_released,proof_failure:audit.proof_failure,cleanup_failure:audit.cleanup_failure}));
  process.exitCode=audit.engine_lifecycle_qualified&&audit.slot_released?0:2;
}
