// Coordinator-only exclusive GUI qualification. No certificate/developer policy changes, no launcher retry.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const source = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(source, '../../..');
const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const planPath = process.env.NEXUS_UWP_PRIVATE_PLAN;
assert.ok(executable && planPath && process.env.NEXUS_UWP_GUI_SLOT === '1', 'Root exclusive GUI slot and exact build/plan required');
const python = path.join(process.env.LOCALAPPDATA, 'Programs/Python/Python314/python.exe');
const plan = JSON.parse(await readFile(planPath, 'utf8'));
const home = path.dirname(planPath), state = path.join(home, 'mcp-state'); await mkdir(state, { recursive: true });
const receiptPath = path.join(home, 'lifecycle-receipt.json');
const receipt = { started_at: new Date().toISOString(), plan: planPath, executable,
  native_sha256: createHash('sha256').update(await readFile(executable)).digest('hex'),
  engine_lifecycle_qualified: false, slot_released: false, no_launcher_retry: true };
const env = { ...process.env, NEXUS_GARDE_FOUS: path.join(project, 'commun/garde-fous.json'),
  NEXUS_WINDOWS_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal') };
delete env.NEXUS_SHARED_HOME; // Real shared emergency stop remains effective during the qualification.
const server = spawn(executable, ['--mcp','--agent','claude'], { windowsHide: true, stdio: ['pipe','pipe','pipe'], env });
const pending = new Map(); let stderr = '', id = 0, hwnd, birth, pid, nativeResumed = false;
server.stderr.on('data', chunk => { stderr += chunk; });
readline.createInterface({ input: server.stdout }).on('line', line => {
  let response; try { response = JSON.parse(line); } catch { return; }
  const waiter = pending.get(response.id); if (waiter) { pending.delete(response.id); clearTimeout(waiter.timer); waiter.resolve(response); }
});
server.on('error', error => { for (const waiter of pending.values()) { clearTimeout(waiter.timer); waiter.reject(error); } pending.clear(); });
async function call(name, args) {
  const current = ++id;
  const wait = new Promise((resolve,reject) => pending.set(current, { resolve,reject,timer:setTimeout(() => { pending.delete(current); reject(new Error(`MCP ${name} timeout: ${stderr}`)); }, 30000) }));
  server.stdin.write(JSON.stringify({ jsonrpc:'2.0',id:current,method:'tools/call',params:{name,arguments:args} })+'\n');
  const response = await wait;
  if(response.error || response.result?.isError) throw new Error(JSON.stringify(response));
  return response.result.structuredContent ?? response.result;
}
const status = () => JSON.parse(execFileSync(python, ['-I','-B',path.join(source,'probe.py'),'--plan',planPath], { windowsHide:true,encoding:'utf8',timeout:20000 }));
const control = action => JSON.parse(execFileSync(python, ['-I','-B',path.join(source,'probe.py'),'--plan',planPath,
  '--action',action,'--apply','--expect-pid',String(pid),'--expect-birth',birth], { windowsHide:true,encoding:'utf8',timeout:20000 }));
const probe = () => JSON.parse(execFileSync(executable, [], { windowsHide:true,encoding:'utf8',timeout:15000,env,input:JSON.stringify({operation:'probe'}) }));
const pause = ms => new Promise(resolve => setTimeout(resolve,ms));
const main = data => data.processes.filter(item => path.resolve(item.image).toLowerCase() === path.resolve(plan.executable).toLowerCase());
const counter = data => {
  const element = data.elements.find(item => item.automationId === 'NexusUwpState');
  assert.ok(element, 'Real XAML status TextBlock must be present');
  const match = /resume=(\d+)/.exec(element.name); assert.ok(match, JSON.stringify(element));
  return { value:Number(match[1]), name:element.name, element };
};
try {
  receipt.before = status(); assert.equal(receipt.before.registered,true); assert.equal(receipt.before.processes.length,0);
  receipt.foreground_before_launch = probe();
  receipt.launch = await call('launch', {app:receipt.before.package.PackageFamilyName+'!App'});
  console.log(JSON.stringify({stage:'launched-once',launch:receipt.launch}));
  let running;
  for(let attempt=0;attempt<8;attempt++) { running=status(); if(main(running).length===1) break; await pause(161.8); }
  receipt.running = running;
  const owned=main(running); assert.equal(owned.length,1,'Exactly one newly launched fixture process required');
  pid=owned[0].pid; birth=owned[0].creation_filetime; assert.equal(owned[0].is_app_container,true);
  const core = running.windows.flatMap(window => [window,...(window.owned_children??[])]).filter(window=>window.pid===pid&&window.class==='Windows.UI.Core.CoreWindow');
  assert.ok(core.length>0,'A real owned CoreWindow must exist');
  const windows=await call('windows',{filter:running.package.PackageFamilyName}); receipt.windows=windows;
  const rows=windows.windows.filter(window=>window.app===running.package.PackageFamilyName+'!App');
  assert.equal(rows.length,1,'Fixture window must be unambiguous'); hwnd=rows[0].id;
  receipt.initial_look=await call('look',{window:hwnd,show:'text'});
  receipt.initial_counter=counter(receipt.initial_look); assert.ok(receipt.initial_look.frame?.width>0&&receipt.initial_look.frame?.height>0,'Initial inspection must have its genuine stable frame');
  await call('window',{window:hwnd,action:'bottom'});
  receipt.foreground_before_suspend=probe(); assert.notEqual(receipt.foreground_before_suspend.foreground,hwnd,'Fixture must be in background');
  let suspended=status();
  if(suspended.state!==3) suspended=receipt.suspend_control=control('suspend');
  receipt.suspended=status(); assert.equal(receipt.suspended.state,3,'Independent OS oracle must attest actual suspension');
  assert.equal(main(receipt.suspended)[0].creation_filetime,birth);
  console.log(JSON.stringify({stage:'actual-state3',pid,birth}));
  receipt.foreground_before_native_resume=probe();
  receipt.resume_look=await call('look',{window:hwnd,show:'text'});
  nativeResumed=receipt.resume_look.storeResume?.Requested===true;
  receipt.after_native_resume=status(); receipt.foreground_after_native_resume=probe();
  assert.equal(nativeResumed,true,'The actual Nexus engine must request Resume');
  assert.equal(receipt.resume_look.storeContentReady,true,'Fresh resumed content must be ready');
  assert.ok(receipt.resume_look.frame?.width>0&&receipt.resume_look.frame?.height>0,'Resumed inspection needs a real stable frame');
  assert.equal(receipt.after_native_resume.state,1,'Independent OS oracle must attest running after the Nexus request');
  const after=main(receipt.after_native_resume); assert.equal(after.length,1); assert.equal(after[0].pid,pid); assert.equal(after[0].creation_filetime,birth);
  receipt.resume_counter=counter(receipt.resume_look);
  assert.equal(receipt.resume_counter.value,receipt.initial_counter.value+1,'Real XAML Resuming event must fire exactly once');
  assert.equal(receipt.foreground_after_native_resume.foreground,receipt.foreground_before_native_resume.foreground,'Nexus must preserve the actual prior foreground');
  receipt.engine_lifecycle_qualified=true;
  console.log(JSON.stringify({stage:'native-resume-qualified',pid,frame:receipt.resume_look.frame,counter:receipt.resume_counter.name}));
} catch(error) { receipt.failure=error.message; console.log(JSON.stringify({stage:'failed',message:error.message})); }
finally {
  try {
    let actual=status();
    if(actual.registered&&main(actual).length===1) {
      pid=main(actual)[0].pid; birth=main(actual)[0].creation_filetime;
      if(actual.state===3||actual.state===2) { receipt.cleanup_resume=control('resume'); actual=status(); }
      if(hwnd) {
        receipt.cleanup_look=await call('look',{window:hwnd,show:'text'});
        receipt.cleanup_close=await call('act',{window:hwnd,then:'none',steps:[{kind:'click',selector:{role:'button',automationId:'NexusUwpClose'},expect:{selector:{role:'text',automationId:'NexusUwpState'},exists:false},timeoutMs:250}]});
      }
      for(let attempt=0;attempt<12;attempt++) { actual=status(); if(actual.processes.length===0) break; await pause(161.8); }
    }
    receipt.after_close=actual;
    if(actual.registered) {
      const family=actual.package.PackageFamilyName;
      assert.ok(family.startsWith(plan.name+'_')&&/^Nexus\.LocalUwpQA\.[a-f0-9]{12}_[a-z0-9]+$/.test(family));
      const diagnosticPath=path.join(process.env.LOCALAPPDATA,'Packages',family,'LocalState','startup.json');
      try { receipt.startup_diagnostic={path:diagnosticPath,data:JSON.parse(await readFile(diagnosticPath,'utf8'))}; }
      catch(error) { receipt.startup_diagnostic={path:diagnosticPath,unavailable:error.message}; }
    }
    if(actual.registered&&actual.processes.length===0) {
      const removed=execFileSync('pwsh.exe',['-NoProfile','-NonInteractive','-File',path.join(source,'register.ps1'),'-Plan',planPath,'-Remove','-Apply'],{encoding:'utf8',windowsHide:true,timeout:20000});
      receipt.removed=JSON.parse(removed); receipt.after_remove=status();
    }
    receipt.slot_released=!receipt.after_remove?.registered&&receipt.after_close?.processes.length===0;
  } catch(error) { receipt.cleanup_failure=error.message; }
  server.stdin.end(); await pause(150); if(server.exitCode===null) server.kill();
  receipt.finished_at=new Date().toISOString(); receipt.stderr=stderr;
  await writeFile(receiptPath,JSON.stringify(receipt,null,2)+'\n');
  console.log(JSON.stringify({receipt:receiptPath,qualified:receipt.engine_lifecycle_qualified,slot_released:receipt.slot_released,failure:receipt.failure,cleanup_failure:receipt.cleanup_failure}));
  process.exitCode=receipt.engine_lifecycle_qualified&&receipt.slot_released?0:2;
}
