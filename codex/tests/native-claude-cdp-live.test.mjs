// Opt-in only: launches a disposable, account-free browser through the Claude MCP launch contract.
// Neither a user's existing browser nor the installed MCP process is restarted.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtemp, rm, readFile, writeFile } from 'node:fs/promises';
import { once } from 'node:events';
import readline from 'node:readline';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { randomUUID, createHash } from 'node:crypto';
import { promisify } from 'node:util';
import { assertRenderedRichText } from './fixtures/claude-cdp-rendered-oracle.mjs';
import { fileArguments } from './fixtures/script-file.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const exe = process.env.NEXUS_NATIVE_TEST_EXE, browser = process.env.NEXUS_CDP_LIVE_BROWSER;
const skip = process.platform !== 'win32' || !exe || !browser;
const execFileAsync=promisify(execFile);
async function closeOwnedFixture(identity){
  // Independent official Win32 close. No action is possible without this run's executable, argv, profile marker,
  // launch-time boundary and, when returned by MCP, exact PID/HWND. The process handle is held across the check/send.
  const source=String.raw`
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FixtureNative {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessageW(IntPtr window,uint message,IntPtr w,IntPtr l);
 public static string[] Arguments(string command) {
  int count; var p=CommandLineToArgvW(command,out count); if(p==IntPtr.Zero) throw new InvalidOperationException("Cannot parse fixture process argv");
  try {var result=new string[count];for(int i=0;i<count;i++)result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return result;}finally{LocalFree(p);}
 }
}
'@
$spec=$env:NEXUS_CDP_FIXTURE_IDENTITY|ConvertFrom-Json
$exePath=[IO.Path]::GetFullPath($spec.executable)
$profileRoot=[IO.Path]::GetFullPath((Join-Path $spec.state 'devtools-profiles')).TrimEnd('\')+'\'
$name=[IO.Path]::GetFileName($exePath).Replace("'","''")
$matches=@()
foreach($row in @(Get-CimInstance Win32_Process -Filter "Name='$name'")) {
 if($row.ExecutablePath -ine $exePath -or !$row.CommandLine){continue}
 $argv=[FixtureNative]::Arguments($row.CommandLine)
 if(@($argv|Where-Object {$_ -ceq ('--app='+$spec.url)}).Count -ne 1){continue}
 $profiles=@($argv|Where-Object {$_.StartsWith('--user-data-dir=',[StringComparison]::Ordinal)})
 if($profiles.Count -ne 1){continue}
 $profile=[IO.Path]::GetFullPath($profiles[0].Substring('--user-data-dir='.Length))
 if(!$profile.StartsWith($profileRoot,[StringComparison]::OrdinalIgnoreCase)){continue}
 if(([IO.File]::GetAttributes($profile) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Fixture profile was replaced by a reparse point'}
 $markerPath=Join-Path $profile '.nexus-profile'
 if(([IO.File]::GetAttributes($markerPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Fixture profile marker is a reparse point'}
 $marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
 if($marker.owner -cne 'nexus-windows-claude' -or $marker.id -cne [IO.Path]::GetFileName($profile)){throw 'Fixture profile ownership marker changed'}
 if($spec.pid -and [int]$spec.pid -ne [int]$row.ProcessId){throw 'Returned fixture PID differs from the exact profile process'}
 $process=[Diagnostics.Process]::GetProcessById([int]$row.ProcessId)
 $heldHandle=$process.Handle
 $birth=$process.StartTime.ToUniversalTime()
 if($spec.started -and $birth -lt [DateTime]::Parse($spec.started).ToUniversalTime().AddMilliseconds(-50)){$process.Dispose();throw 'Fixture process predates this launch'}
 $matches+=@{Process=$process;Birth=$birth;Profile=$profile}
}
if($matches.Count -gt 1){foreach($entry in $matches){$entry.Process.Dispose()};throw 'Multiple exact fixture processes; no window closed'}
if($matches.Count -eq 0){
 if($spec.hwnd -and [FixtureNative]::IsWindow([IntPtr][long]$spec.hwnd)){throw 'Fixture HWND still exists but its exact process/profile is no longer attributable'}
 @{closed=$true;running=$false;matched=0}|ConvertTo-Json -Compress;exit
}
$entry=$matches[0];$process=$entry.Process
try {
 $window=if($spec.hwnd){[IntPtr][long]$spec.hwnd}else{$process.MainWindowHandle}
 if($window -ne [IntPtr]::Zero -and [FixtureNative]::IsWindow($window)){
  [uint32]$ownerPid=0;[void][FixtureNative]::GetWindowThreadProcessId($window,[ref]$ownerPid)
  if($ownerPid -ne $process.Id -or $process.HasExited -or $process.StartTime.ToUniversalTime() -ne $entry.Birth){throw 'Fixture window/process identity changed before close'}
  if(![FixtureNative]::PostMessageW($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)){throw 'WM_CLOSE refused for the exact fixture HWND'}
 }
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while($clock.ElapsedMilliseconds -lt 5000 -and !$process.HasExited){[Threading.Thread]::Sleep(50)}
 $closed=$window -eq [IntPtr]::Zero -or ![FixtureNative]::IsWindow($window)
 @{closed=$closed;running=(!$process.HasExited);matched=1;pid=$process.Id;hwnd=$window.ToInt64();birth=$entry.Birth.ToString('o')}|ConvertTo-Json -Compress
} finally {$process.Dispose()}
`;
  const result=await execFileAsync('powershell.exe',fileArguments(source),{
    windowsHide:true,timeout:10000,maxBuffer:1024*1024,env:{...process.env,NEXUS_CDP_FIXTURE_IDENTITY:JSON.stringify(identity)},
  });
  return JSON.parse(result.stdout);
}
test('native Claude disposable CDP: multiline/rich events, exact select, DOM shortcuts, hover and drag', {skip,timeout:120000}, async t => {
  const root = path.resolve(directory,'../..');
  const state = await mkdtemp(path.join(os.tmpdir(),'nexus-claude-cdp-live-'));
  const runId=randomUUID();
  await writeFile(path.join(state,'.nexus-cdp-fixture.json'),JSON.stringify({owner:'nexus-cdp-local-acceptance',run:runId}));
  const html = (await readFile(path.join(directory,'fixtures','claude-cdp.html'),'utf8')).replace('__NEXUS_FIXTURE_RUN__',runId);
  const diagnostic=(stage,details={})=>process.stderr.write('[CDP fixture] '+JSON.stringify({stage,...details})+'\n');
  let readyResolve,readyReject;const ready=new Promise((resolve,reject)=>{readyResolve=resolve;readyReject=reject;});
  ready.catch(()=>{});
  let url,owned,launchedAt,launchBaseline,primaryError;
  const events=[];
  const http = createServer(async (req,res) => {
    if(req.url==='/ready'&&req.method==='POST'){
      let body='';for await(const chunk of req)body+=chunk;
      const page=JSON.parse(body);if(page.run===runId&&page.href===url&&page.title==='Nexus Claude CDP fixture'){
        diagnostic('page-ready',page);readyResolve(page);res.writeHead(204).end();
      }else res.writeHead(400).end();return;
    }
    if(req.url==='/oracle'&&req.method==='POST'){
      let body='';for await(const chunk of req)body+=chunk;
      events.push(JSON.parse(body));res.writeHead(204).end();return;
    }
    if(req.url==='/'){res.writeHead(200,{'content-type':'text/html;charset=utf-8','content-security-policy':"default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'"});res.end(html);return;}
    res.writeHead(404).end();
  });
  http.listen(0,'127.0.0.1');await once(http,'listening');url=`http://127.0.0.1:${http.address().port}/`;
  diagnostic('http-listening',{url,state});
  const child=spawn(exe,['--mcp','--agent','claude'],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,
    NEXUS_WINDOWS_HOME:state,NEXUS_SHARED_HOME:state,NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(state,'journal'),NEXUS_CDP_FIXTURE_DIAGNOSTICS:runId,
    NEXUS_GARDE_FOUS:path.join(root,'commun','garde-fous.json')}});
  const waiting=new Map();let id=0,stderr='',hwnd,childEnded=false;
  function rejectPending(reason){for(const pending of waiting.values()){clearTimeout(pending.timer);pending.reject(reason);}waiting.clear();}
  child.once('error',error=>{childEnded=true;diagnostic('mcp-error',{message:error.message});rejectPending(error);readyReject(error);});
  child.once('exit',(code,signal)=>{childEnded=true;diagnostic('mcp-exit',{code,signal});rejectPending(new Error(`MCP exited (${code??signal})\n${stderr}`));});
  child.stdin.on('error',error=>rejectPending(error));
  const aborted=()=>{const error=new Error('Fixture acceptance timed out/aborted');diagnostic('test-aborted');rejectPending(error);readyReject(error);};
  t.signal.addEventListener('abort',aborted,{once:true});
  let nativeDiagnostic='';const wire=[];
  child.stderr.on('data',data=>{
    stderr+=data;nativeDiagnostic+=data;
    const lines=nativeDiagnostic.split('\n');nativeDiagnostic=lines.pop();
    for(const line of lines)if(line.startsWith('{"fixture":"claude-cdp-ancestry-diff"')||line.startsWith('{"fixture":"claude-cdp-wire"')){
      if(line.startsWith('{"fixture":"claude-cdp-wire"'))wire.push(JSON.parse(line));
      process.stderr.write('[CDP native] '+line+'\n');
    }
  });
  const lines=readline.createInterface({input:child.stdout});lines.on('line',line=>{
    let value;try{value=JSON.parse(line);}catch{return;}
    const pending=waiting.get(value.id);if(pending){waiting.delete(value.id);clearTimeout(pending.timer);pending.resolve(value);}
  });
  async function rpc(method,params={},cleanup=false){
    if(childEnded||child.exitCode!==null)throw new Error('Fixture MCP is no longer running');
    if(t.signal.aborted&&!cleanup)throw new Error('Fixture acceptance aborted');
    const current=++id;
    const pending=new Promise((resolve,reject)=>waiting.set(current,{resolve,reject,timer:setTimeout(()=>{waiting.delete(current);reject(new Error('MCP timeout: '+stderr));},cleanup?5000:30000)}));
    child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method,params})+'\n');
    const result=await pending;if(result.error)throw new Error(JSON.stringify(result.error)+'\nFixture MCP stderr:\n'+stderr);return result.result;
  }
  async function call(name,args,cleanup=false){diagnostic('call-start',{name});const result=await rpc('tools/call',{name,arguments:args},cleanup);assert.ok(!result.isError,`${name}: ${JSON.stringify(result)}\nFixture MCP stderr:\n${stderr}`);diagnostic('call-end',{name});return result.structuredContent??result;}
  async function helper(request){
    return await new Promise((resolve,reject)=>{
      const p=spawn(exe,[],{windowsHide:true,stdio:['pipe','pipe','pipe']});let out='',error='';
      const timer=setTimeout(()=>{p.kill();reject(new Error('Independent native probe timed out'));},10000);
      p.stdout.on('data',b=>{out+=b;});p.stderr.on('data',b=>{error+=b;});p.stdin.on('error',reject);
      p.once('error',e=>{clearTimeout(timer);reject(e);});
      p.once('close',code=>{clearTimeout(timer);try{if(code)throw new Error(error||out);resolve(JSON.parse(out));}catch(e){reject(e);}});
      p.stdin.end(JSON.stringify(request));
    });
  }
  const probe=()=>helper({operation:'probe'});
  const unchanged=(before,after,label)=>{
    assert.equal(after.foreground,before.foreground,`${label}: foreground changed`);
    assert.deepEqual(after.cursor,before.cursor,`${label}: cursor moved`);
  };
  const measures=[];
  const look=()=>call('look',{hwnd,show:'both'});
  async function stableUia(){
    const deadline=performance.now()+5000;
    const wanted=['Notes','Éditeur','Notes ombre','Couleur','Survol','Départ','Arrivée'];
    let previous,reads=0,lastCount=0;
    do{
      // Reads alone may be repeated. The last complete observation is the one used by the single following act.
      const observation=await look();reads++;
      const elements=observation.elements.filter(element=>!element.ocr);lastCount=elements.length;
      const published=wanted.every(name=>elements.filter(element=>element.name===name&&element.role!=='text'&&element.visible&&!element.disabled).length===1);
      const identity={window:observation.window,app:observation.app,processId:observation.processId,processStartedUtc:observation.processStartedUtc,
        binaryRevision:observation.revision,frame:observation.frame,
        nodes:elements.map(element=>[element.index,element.depth,element.role,element.name,element.automationId,
          element.runtimeId,element.nativeWindow,element.ancestors,element.bounds,element.disabled,element.password])};
      const revision=createHash('sha256').update(JSON.stringify(identity)).digest('hex');
      if(published&&revision===previous){diagnostic('uia-ready',{reads,nodeCount:lastCount,revision,consecutiveEqualObservations:2});return observation;}
      previous=published?revision:undefined;
    }while(performance.now()<deadline&&!t.signal.aborted);
    throw new Error(`UIA publication did not reach two complete equal observations within readiness budget (${reads} reads, ${lastCount} nodes); no act sent`);
  }
  async function act(step){
    diagnostic('scenario-start',{action:step.do,target:step.target??null});
    await stableUia();const before=await probe(),started=performance.now();
    assert.notEqual(before.foreground,hwnd,'Acceptance requires the disposable target to remain in background');
    const result=await call('act',{hwnd,steps:[step],then:'none'});
    const after=await probe();unchanged(before,after,step.do);
    assert.equal(result.status,'success',JSON.stringify(result));
    assert.ok(result.steps.every(s=>String(s.technique??s.method??s.detail??JSON.stringify(s)).toLowerCase().includes('devtools')),JSON.stringify(result));
    assert.ok(result.steps.every(s=>s.foregroundTaken!==true),'CDP gesture reported taking foreground');
    measures.push({action:step.do,ms:Math.round(performance.now()-started),foregroundStable:true,cursorStable:true});diagnostic('scenario-end',measures.at(-1));return result;
  }
  try{
    await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'native-cdp-local-acceptance',version:'1'}});
    const beforeLaunch=await probe();launchBaseline=beforeLaunch;launchedAt=new Date().toISOString();
    const launched=await call('launch',{app:browser,args:`--window-size=960,1000 --app=${url}`,devtools:true,then:'none'});
    hwnd=launched.windows?.[0]?.id??launched.window;assert.ok(hwnd,JSON.stringify(launched));
    owned=launched.windows?.[0]??{id:hwnd};
    const readyTimer=setTimeout(()=>readyReject(new Error('The exact local fixture page never reported readiness')),15000);
    try{await ready;}finally{clearTimeout(readyTimer);}
    unchanged(beforeLaunch,await probe(),'disposable launch');
    const notes='ligne1\nligne2';
    await act({do:'set',target:'Notes',text:notes,check:{target:'Notes',value:notes}});
    const rich='Bonjour\n\nJulien 😀';
    await act({do:'set',target:'Éditeur',text:rich,check:{target:'Éditeur',value:rich}});
    await act({do:'type',target:'Éditeur',text:' suite',check:{target:'Éditeur',value:rich+' suite'}});
    await act({do:'set',target:'Notes ombre',text:'Ombre\n😀',check:{target:'Notes ombre',value:'Ombre\n😀'}});
    await act({do:'select',target:'Couleur',option:'Bleu',check:{target:'Couleur',value:'Bleu'}});
    await act({do:'key',keys:'ctl+k',expect:'Ctrl+K reçu'});
    await act({do:'key',keys:'alt+f24',expect:'Alt+F24 reçu'});
    await act({do:'hover',target:'Survol',expect:'Survol reçu'});
    await act({do:'drag',target:'Départ',to:'Arrivée',expect:'Déplacement reçu'});
    diagnostic('dom-oracle',{events,measures});t.diagnostic(JSON.stringify({oracle:'independent localhost scalar DOM events',events,measures}));
    const lastInput=target=>events.filter(e=>e.kind==='input'&&e.target===target).at(-1);
    function logicalInsert(target,texts){
      const commits=events.filter(e=>e.kind==='beforeinput'&&e.target===target);
      assert.deepEqual(commits.map(e=>e.data),texts,'Each logical insertion must reach beforeinput once, with its exact text');
      assert.ok(commits.every(e=>e.trusted&&e.inputType==='insertText'));
      assert.ok(events.some(e=>e.kind==='input'&&e.target===target&&e.trusted),'Native input event missing');
    }
    logicalInsert('notes',[notes]);logicalInsert('editor',[rich,' suite']);logicalInsert('shadow-notes',['Ombre\n😀']);
    const commits=events.filter(e=>e.kind==='beforeinput'&&['notes','editor','shadow-notes'].includes(e.target));
    const sent=wire.filter(e=>e.phase==='sent'),ack=wire.filter(e=>e.phase==='acknowledged');
    assert.equal(sent.length,commits.length,'One CDP insertText packet is required per independent logical commit');
    assert.deepEqual(sent.map(e=>e.textLength).sort((a,b)=>a-b),commits.map(e=>e.data.length).sort((a,b)=>a-b));
    assert.deepEqual(ack.map(e=>e.session+':'+e.id).sort(),sent.map(e=>e.session+':'+e.id).sort(),'Every insertText must have one correlated acknowledgement');
    assert.equal(new Set(sent.map(e=>e.session+':'+e.id)).size,sent.length);
    assert.equal(lastInput('notes').value,notes);
    assert.equal(lastInput('shadow-notes').value,'Ombre\n😀');
    const rendered=assertRenderedRichText(lastInput('editor').richSnapshot,rich+' suite');
    diagnostic('rendered-rich-text-proved',rendered);
    assert.deepEqual(events.filter(e=>e.target==='color').map(e=>[e.kind,e.value]),[['input','blue'],['change','blue']]);
    assert.equal(events.filter(e=>e.kind==='shortcut').length,1);
    assert.equal(events.filter(e=>e.kind==='key'&&e.key==='F24'&&e.alt).length,1);
    assert.equal(events.filter(e=>e.kind==='drag-down').length,1);assert.equal(events.filter(e=>e.kind==='drag-up').length,1);
    t.diagnostic(JSON.stringify({oracle:'independent localhost events + native process probes',events,measures,limits:'single rendered surface; Steam multiview excluded'}));
  }catch(error){primaryError=error;diagnostic('primary-error',{name:error.name,message:error.message});}finally{
    let closeError;
    diagnostic('cleanup-start',{hwnd:hwnd??null,childEnded});
    let beforeClose;try{beforeClose=await probe();}catch(error){diagnostic('cleanup-probe-unavailable',{message:error.message});}
    if(hwnd&&!childEnded)try{await call('window',{hwnd,action:'close'},true);}catch(error){closeError=error;diagnostic('mcp-close-failed',{message:error.message});}
    let closed;
    try{closed=await closeOwnedFixture({executable:browser,state,url,started:launchedAt,pid:owned?.pid,hwnd});diagnostic('independent-close',closed);
      if(!closed.closed)throw new Error('The exact disposable fixture window did not close');closeError=null;
    }catch(error){closeError=error;diagnostic('independent-close-failed',{message:error.message});}
    if(beforeClose)try{
      if(beforeClose.foreground===hwnd&&launchBaseline?.foreground&&launchBaseline.foreground!==hwnd){
        // Closing a fixture that unexpectedly took focus should return to the validated prelaunch baseline.
        // The original launch/gesture assertion remains the primary failure; this is cleanup proof only.
        const clock=performance.now();let after;
        do{after=await probe();if(after.foreground===launchBaseline.foreground&&after.foregroundPid===launchBaseline.foregroundPid)break;
          if(performance.now()-clock>=1618)break;await new Promise(resolve=>setTimeout(resolve,20));
        }while(true);
        assert.equal(after.foreground,launchBaseline.foreground,'Cleanup did not return to the original baseline window');
        assert.equal(after.foregroundPid,launchBaseline.foregroundPid,'Cleanup baseline process differs');
        assert.deepEqual(after.cursor,beforeClose.cursor,'Cleanup moved the cursor');
        diagnostic('cleanup-baseline-restored',{foreground:after.foreground,foregroundPid:after.foregroundPid});
      }else unchanged(beforeClose,await probe(),'disposable close');
    }catch(error){closeError??=error;diagnostic('cleanup-probe-failed',{message:error.message});}
    rejectPending(new Error('Fixture cleanup'));t.signal.removeEventListener('abort',aborted);
    child.stdin.end();
    http.closeAllConnections();await new Promise(resolve=>http.close(resolve));diagnostic('http-closed');
    if(child.exitCode==null){await Promise.race([once(child,'exit'),new Promise(resolve=>setTimeout(resolve,1618))]);if(child.exitCode==null)child.kill();}
    lines.close();child.stdout.destroy();child.stderr.destroy();child.stdin.destroy();
    if(closed?.closed&&!closed.running){try{await rm(state,{recursive:true,force:true,maxRetries:0});diagnostic('state-removed');}catch(error){closeError??=error;diagnostic('state-remove-failed',{message:error.message});}}
    else diagnostic('state-retained',{state,reason:'Browser/profile cleanup not fully proved; no recursive deletion attempted'});
    diagnostic('cleanup-finished');
    if(primaryError&&closeError)throw new AggregateError([primaryError,closeError],'CDP fixture failed; cleanup also failed',{cause:primaryError});
    if(primaryError)throw primaryError;
    if(closeError)throw closeError;
  }
});
