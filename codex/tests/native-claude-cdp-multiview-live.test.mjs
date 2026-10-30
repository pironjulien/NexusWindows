// Opt-in qualification on existing Electron only. No Steam/user session is restarted or changed.
import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn,execFile} from 'node:child_process';
import {createServer} from 'node:http';
import {once} from 'node:events';
import {mkdtemp,writeFile,rm} from 'node:fs/promises';
import readline from 'node:readline';
import path from 'node:path';
import os from 'node:os';
import {fileURLToPath} from 'node:url';
import {randomUUID} from 'node:crypto';
import {promisify} from 'node:util';

const directory=path.dirname(fileURLToPath(import.meta.url));
const exe=process.env.NEXUS_NATIVE_TEST_EXE;
const electron=process.env.NEXUS_CDP_MULTIVIEW_ELECTRON??path.join(os.homedir(),'.claude','nexus-claude','lab','electron','node_modules','electron','dist','electron.exe');
const skip=process.platform!=='win32'||!exe||process.env.NEXUS_CDP_MULTIVIEW_TEST!=='1';
const executeFile=promisify(execFile);
async function closeFixture(spec){
  const script=String.raw`
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MultiFixtureNative {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string c,out int n);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static string[] Arguments(string c){int n;var p=CommandLineToArgvW(c,out n);if(p==IntPtr.Zero)throw new Exception("argv unavailable");try{var a=new string[n];for(int i=0;i<n;i++)a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return a;}finally{LocalFree(p);}}
}
'@
$spec=$env:NEXUS_MULTIVIEW_IDENTITY|ConvertFrom-Json
$markerPath=Join-Path $spec.state '.nexus-cdp-fixture.json'
if(([IO.File]::GetAttributes($spec.state) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or ([IO.File]::GetAttributes($markerPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Fixture state or marker is a reparse point'}
$marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
if($marker.owner -cne 'nexus-cdp-multiview-fixture' -or $marker.run -cne $spec.run){throw 'Fixture marker changed'}
$exePath=[IO.Path]::GetFullPath($spec.executable);$name=[IO.Path]::GetFileName($exePath).Replace("'","''");$owned=@()
foreach($row in @(Get-CimInstance Win32_Process -Filter "Name='$name'")){
 if($row.ExecutablePath -ine $exePath -or !$row.CommandLine){continue}
 $argv=[MultiFixtureNative]::Arguments($row.CommandLine)
 if(!($argv -ccontains $spec.fixture) -or !($argv -ccontains ('--nexus-cdp-run='+$spec.run)) -or !($argv -ccontains ('--nexus-cdp-state='+$spec.state))){continue}
 if($argv|Where-Object {$_.StartsWith('--type=',[StringComparison]::Ordinal)}){continue}
 $p=[Diagnostics.Process]::GetProcessById([int]$row.ProcessId);$held=$p.Handle;$birth=$p.StartTime.ToUniversalTime()
 if($spec.pid -and [int]$spec.pid -ne $p.Id){$p.Dispose();throw 'Exact fixture PID differs'}
 if($spec.started -and $birth -lt [DateTime]::Parse($spec.started).ToUniversalTime().AddMilliseconds(-50)){$p.Dispose();throw 'Exact fixture predates launch'}
 $owned+=@{Process=$p;Birth=$birth}
}
if($owned.Count -gt 1){foreach($item in $owned){$item.Process.Dispose()};throw 'Ambiguous exact fixture process'}
if($owned.Count -eq 0){if($spec.hwnd -and [MultiFixtureNative]::IsWindow([IntPtr][long]$spec.hwnd)){throw 'Fixture HWND exists without its exact process identity'};@{closed=$true;running=$false;matched=0}|ConvertTo-Json -Compress;exit}
$item=$owned[0];$p=$item.Process
try{
 $window=if($spec.hwnd){[IntPtr][long]$spec.hwnd}else{$p.MainWindowHandle}
 if($window -ne [IntPtr]::Zero -and [MultiFixtureNative]::IsWindow($window)){
  [uint32]$ownerPid=0;[void][MultiFixtureNative]::GetWindowThreadProcessId($window,[ref]$ownerPid)
  if($ownerPid -ne $p.Id -or $p.HasExited -or $p.StartTime.ToUniversalTime() -ne $item.Birth){throw 'Fixture HWND/process changed before close'}
  if(![MultiFixtureNative]::PostMessageW($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'Exact fixture WM_CLOSE refused'}
 }
 $clock=[Diagnostics.Stopwatch]::StartNew();while($clock.ElapsedMilliseconds -lt 5000 -and !$p.HasExited){[Threading.Thread]::Sleep(50)}
 @{closed=($window -eq [IntPtr]::Zero -or ![MultiFixtureNative]::IsWindow($window));running=(!$p.HasExited);pid=$p.Id;hwnd=$window.ToInt64()}|ConvertTo-Json -Compress
}finally{$p.Dispose()}
`;
  const result=await executeFile('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,timeout:10000,env:{...process.env,NEXUS_MULTIVIEW_IDENTITY:JSON.stringify(spec)}});
  return JSON.parse(result.stdout);
}
test('native Claude multiview CDP: one HWND, exact documents, HTTP value/event oracle and zero other-surface input', {skip,timeout:120000},async t=>{
  const root=path.resolve(directory,'../..'),run=randomUUID();
  const state=await mkdtemp(path.join(os.tmpdir(),'nexus-cdp-multiview-'));
  await writeFile(path.join(state,'.nexus-cdp-fixture.json'),JSON.stringify({owner:'nexus-cdp-multiview-fixture',run}));
  const fixture=path.join(directory,'fixtures','claude-cdp-multiview.cjs');
  const diagnostic=(stage,details={})=>process.stderr.write('[CDP multiview] '+JSON.stringify({stage,...details})+'\n');
  let readyResolve,readyReject;const ready=new Promise((resolve,reject)=>{readyResolve=resolve;readyReject=reject;});ready.catch(()=>{});
  const received=[];
  const server=createServer(async(req,res)=>{
    try{
      if(req.method!=='POST'||req.url!=='/oracle'){res.writeHead(404).end();return;}
      let body='';for await(const chunk of req)body+=chunk;
      const record=JSON.parse(body);if(record.run!==run){res.writeHead(403).end();return;}
      received.push(record);if(record.kind==='ready'){readyResolve(record);diagnostic('fixture-ready',record);}res.writeHead(204).end();
    }catch{res.destroy();}
  });
  server.on('clientError',(_error,socket)=>socket.destroy());
  server.listen(0,'127.0.0.1');await once(server,'listening');
  const oracle=`http://127.0.0.1:${server.address().port}/oracle`;
  const child=spawn(exe,['--mcp','--agent','claude'],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,NEXUS_WINDOWS_HOME:state,NEXUS_SHARED_HOME:state,NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(state,'journal'),NEXUS_GARDE_FOUS:path.join(root,'commun','garde-fous.json')}});
  const pending=new Map();let id=0,ended=false,stderr='',hwnd,own,started;
  const fail=error=>{for(const waiter of pending.values()){clearTimeout(waiter.timer);waiter.reject(error);}pending.clear();};
  child.on('error',error=>{ended=true;fail(error);readyReject(error);});
  child.on('exit',(code,signal)=>{ended=true;diagnostic('mcp-exit',{code,signal});fail(new Error(`MCP exited ${code??signal}: ${stderr}`));});
  child.stdin.on('error',fail);child.stderr.on('data',data=>{stderr+=data;});
  const lines=readline.createInterface({input:child.stdout});lines.on('line',line=>{let result;try{result=JSON.parse(line);}catch{return;}const waiter=pending.get(result.id);if(waiter){pending.delete(result.id);clearTimeout(waiter.timer);waiter.resolve(result);}});
  const abort=()=>{const error=new Error('Multiview test aborted');fail(error);readyReject(error);diagnostic('aborted');};t.signal.addEventListener('abort',abort,{once:true});
  async function rpc(method,params={},cleanup=false){
    if(ended||child.exitCode!==null)throw new Error('MCP child no longer running');if(t.signal.aborted&&!cleanup)throw new Error('Test aborted');
    const current=++id,wait=new Promise((resolve,reject)=>pending.set(current,{resolve,reject,timer:setTimeout(()=>{pending.delete(current);reject(new Error('MCP timeout: '+stderr));},cleanup?5000:30000)}));
    child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method,params})+'\n');const reply=await wait;if(reply.error)throw new Error(JSON.stringify(reply.error));return reply.result;
  }
  async function call(name,args,cleanup=false){diagnostic('call-start',{name});const result=await rpc('tools/call',{name,arguments:args},cleanup);assert.ok(!result.isError,`${name}: ${JSON.stringify(result)}\n${stderr}`);diagnostic('call-end',{name});return result.structuredContent??result;}
  async function probe(){return await new Promise((resolve,reject)=>{
    const p=spawn(exe,[],{windowsHide:true,stdio:['pipe','pipe','pipe']});let text='',error='';const timer=setTimeout(()=>{p.kill();reject(new Error('Probe timeout'));},10000);
    p.stdout.on('data',b=>{text+=b;});p.stderr.on('data',b=>{error+=b;});p.stdin.on('error',reject);p.on('error',e=>{clearTimeout(timer);reject(e);});p.on('close',code=>{clearTimeout(timer);try{if(code)throw new Error(error||text);resolve(JSON.parse(text));}catch(e){reject(e);}});p.stdin.end(JSON.stringify({operation:'probe'}));
  });}
  function stable(before,after,label){assert.equal(after.foreground,before.foreground,label+' foreground');assert.deepEqual(after.cursor,before.cursor,label+' cursor');}
  let control;const measures=[];
  async function snapshot(){const response=await fetch(control,{signal:AbortSignal.timeout(5000)});assert.equal(response.status,200);const result=await response.json();assert.equal(result.run,run);assert.equal(result.hwnd,hwnd);return result;}
  async function gesture(side,action,text,expected){
    const other=side==='gauche'?'droite':'gauche',beforeOracle=await snapshot();
    const beforeSide=beforeOracle.surfaces.find(s=>s.side===side),beforeOther=beforeOracle.surfaces.find(s=>s.side===other);
    const seen=await call('look',{hwnd,show:'both'});
    for(const name of ['Notes gauche','Notes droite'])assert.equal(seen.elements.filter(e=>e.name===name&&e.role==='edit').length,1,'Each exact target must be visible once in the same HWND');
    const before=await probe();assert.notEqual(before.foreground,hwnd,'Target must remain in background');const clock=performance.now();
    const result=await call('act',{hwnd,then:'none',steps:[{do:action,target:'Notes '+side,text,check:{target:'Notes '+side,value:expected}}]});
    stable(before,await probe(),side+' '+action);assert.equal(result.status,'success',JSON.stringify(result));assert.ok(result.steps.every(s=>s.technique==='background:devtools'&&s.foregroundTaken!==true),JSON.stringify(result));
    const after=await snapshot(),aimed=after.surfaces.find(s=>s.side===side),untouched=after.surfaces.find(s=>s.side===other);
    assert.equal(aimed.webContentsId,beforeSide.webContentsId);assert.equal(untouched.webContentsId,beforeOther.webContentsId);
    assert.equal(aimed.snapshot.value,expected,'Independent DOM value differs');
    assert.equal(untouched.snapshot.value,beforeOther.snapshot.value,'The other webContents value changed');
    const otherEvents=untouched.snapshot.events.slice(beforeOther.snapshot.events.length);
    assert.deepEqual(otherEvents.filter(e=>e.kind==='input'||e.kind==='beforeinput'),[],'The other webContents received text input');
    const fresh=aimed.snapshot.events.slice(beforeSide.snapshot.events.length);
    assert.ok(fresh.some(e=>e.kind==='input'&&e.trusted&&e.value===expected),'The intended surface must receive a trusted editor input');
    measures.push({side,action,ms:Math.round(performance.now()-clock),value:expected,targetEvents:fresh,otherInputEventsAdded:0,otherEvents,foregroundStable:true,cursorStable:true});diagnostic('gesture-proved',measures.at(-1));
  }
  try{
    await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'native-cdp-multiview',version:'1'}});
    const before=await probe();started=new Date().toISOString();
    const launched=await call('launch',{app:electron,args:`"${fixture}" --nexus-cdp-run=${run} "--nexus-cdp-state=${state}" --nexus-cdp-oracle=${oracle}`,devtools:true,then:'none'});
    own=launched.windows?.[0];hwnd=own?.id??launched.window;assert.ok(hwnd,JSON.stringify(launched));
    const timer=setTimeout(()=>readyReject(new Error('Two-view fixture readiness not received')),15000);let attested;try{attested=await ready;}finally{clearTimeout(timer);}
    assert.equal(attested.hwnd,hwnd);assert.equal(attested.surfaces.length,2);assert.equal(new Set(attested.surfaces.map(s=>s.webContentsId)).size,2);
    const endpoint=new URL(attested.control);assert.equal(endpoint.protocol,'http:');assert.equal(endpoint.hostname,'127.0.0.1');assert.equal(endpoint.searchParams.get('run'),run);control=endpoint.href;
    stable(before,await probe(),'launch');
    const initial=await snapshot();for(const surface of initial.surfaces)assert.deepEqual(surface.snapshot,{value:'',events:[]});
    await gesture('gauche','set','Gauche\nalpha 😀','Gauche\nalpha 😀');
    await gesture('droite','set','Droite beta','Droite beta');
    await gesture('gauche','type',' suite','Gauche\nalpha 😀 suite');
    await gesture('droite','type',' delta','Droite beta delta');
    diagnostic('acceptance-proved',{hwnd,measures,received});t.diagnostic(JSON.stringify({hwnd,measures,oracle:'independent fixture HTTP snapshots + IPC sender attribution',steam:'This exercises document/surface attribution, not the real Steam session'}));
  }finally{
    diagnostic('cleanup-start',{hwnd:hwnd??null});let closeError,closed;
    if(hwnd&&!ended)try{await call('window',{hwnd,action:'close'},true);}catch(error){diagnostic('mcp-close-failed',{message:error.message});}
    try{closed=await closeFixture({executable:electron,state,run,fixture,started,pid:own?.pid,hwnd});diagnostic('independent-close',closed);assert.ok(closed.closed,'Exact multiview fixture window remains open');}catch(error){closeError=error;diagnostic('close-failed',{message:error.message});}
    fail(new Error('Fixture cleanup'));t.signal.removeEventListener('abort',abort);child.stdin.end();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
    if(child.exitCode===null){await Promise.race([once(child,'exit'),new Promise(resolve=>setTimeout(resolve,1618))]);if(child.exitCode===null)child.kill();}
    lines.close();child.stdin.destroy();child.stdout.destroy();child.stderr.destroy();
    if(closed?.closed&&!closed.running)await rm(state,{recursive:true,force:true,maxRetries:0});else diagnostic('state-retained',{state});
    diagnostic('cleanup-finished');if(closeError)throw closeError;
  }
});
