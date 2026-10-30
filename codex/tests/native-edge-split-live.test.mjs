// Edge Split only: this is not Electron/Steam qualification. Opt in under an exclusive desktop slot.
import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp,writeFile,rm,readFile} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {createEdgeSplitFixture} from './fixtures/edge-split-fixture.mjs';
import {checkEdgeSplitPrerequisites} from './fixtures/edge-split-prerequisites.mjs';
import {nativeFixtureClient,nativeProbe,ownedEdgeWindow,cdpRead} from './fixtures/edge-split-runtime.mjs';

const exe=process.env.NEXUS_NATIVE_TEST_EXE,browser=process.env.NEXUS_EDGE_SPLIT_BROWSER;
const skip=process.platform!=='win32'||!exe||!browser||process.env.NEXUS_EDGE_SPLIT_TEST!=='1';
test('Edge Split: two real CDP pages/UIA documents/surfaces in one HWND, four background writes', {skip,timeout:600000},async t=>{
  const diagnostic=(stage,details={})=>process.stderr.write('[Edge Split] '+JSON.stringify({stage,...details})+'\n');
  const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
  // This acceptance run explicitly pins the immutable engine already qualified by the parent.
  assert.equal(createHash('sha256').update(await readFile(path.join(path.dirname(exe),'NexusWindows.dll'))).digest('hex'),'35357cefadf4cc3116017f356dde6d1d78d7d97679f616fec577c38aadf5edf3','Checkpoint10 assembly required');
  // Existing engine diagnostic opt-in accepts this exact local-fixture marker. It grants no input lease.
  const fixture=await createEdgeSplitFixture(),state=await mkdtemp(path.join(os.tmpdir(),'nexus-claude-cdp-live-edge-split-'));
  await writeFile(path.join(state,'.nexus-cdp-fixture.json'),JSON.stringify({owner:'nexus-cdp-local-acceptance',run:fixture.run}));
  const client=nativeFixtureClient(exe,{NEXUS_WINDOWS_HOME:state,NEXUS_SHARED_HOME:state,NEXUS_ROUTINES_MIRROR:'',NEXUS_JOURNAL_HOME:path.join(state,'journal'),NEXUS_GARDE_FOUS:path.join(root,'commun','garde-fous.json'),NEXUS_CDP_FIXTURE_DIAGNOSTICS:fixture.run},diagnostic);
  const abort=()=>client.fail(new Error('Edge Split test aborted'));t.signal.addEventListener('abort',abort,{once:true});
  const probe=()=>nativeProbe(exe),call=(name,args)=>client.call(name,args);
  let hwnd,bound,identity,baseline,primaryError,proof,setupSummary;
  const measures=[];
  const unchanged=(a,b,label)=>{assert.equal(b.foreground,a.foreground,label+' foreground');assert.equal(b.foregroundPid,a.foregroundPid,label+' foreground process');assert.deepEqual(b.cursor,a.cursor,label+' cursor');};
  async function stableUia(){let previous,reads=0;const deadline=performance.now()+5000;
    do{const seen=await call('look',{hwnd,show:'both'});reads++;const elements=seen.elements.filter(e=>!e.ocr);
      const complete=['Notes gauche','Notes droite'].every(name=>elements.filter(e=>e.name===name&&e.role==='edit'&&e.visible&&!e.disabled).length===1);
      const hash=createHash('sha256').update(JSON.stringify({window:seen.window,app:seen.app,pid:seen.processId,birth:seen.processStartedUtc,revision:seen.revision,frame:seen.frame,nodes:elements.map(e=>[e.index,e.depth,e.role,e.name,e.automationId,e.runtimeId,e.nativeWindow,e.ancestors,e.bounds,e.disabled,e.password])})).digest('hex');
      if(complete&&hash===previous){diagnostic('uia-stable',{reads,hash});return seen;}previous=complete?hash:undefined;
    }while(performance.now()<deadline&&!t.signal.aborted);throw new Error('Two complete identical UIA observations were not published; no input sent');}
  async function evidence(uia){
    const native=await ownedEdgeWindow(identity),base='http://127.0.0.1:'+identity.port;
    const targets=await (await fetch(base+'/json/list',{signal:AbortSignal.timeout(5000)})).json();
    const version=await (await fetch(base+'/json/version',{signal:AbortSignal.timeout(5000)})).json();
    const metrics={},windowForTarget={};
    for(const url of fixture.urls){const page=targets.find(p=>p.type==='page'&&p.url===url);assert.ok(page,'Real local page missing');
      windowForTarget[page.id]=await cdpRead(version.webSocketDebuggerUrl,'Browser.getWindowForTarget',{targetId:page.id});
      const value=await cdpRead(page.webSocketDebuggerUrl,'Runtime.evaluate',{expression:'JSON.stringify({href:location.href,title:document.title,innerWidth,innerHeight,dpr:devicePixelRatio})',returnByValue:true});metrics[page.id]=JSON.parse(value.result.value);
    }
    return {expected:{urls:fixture.urls,titles:fixture.titles},bound,uia,targets,metrics,windowForTarget,surfaces:native.surfaces};
  }
  async function gesture(side,action,text,expected){
    const other=side==='gauche'?'droite':'gauche',beforeOracle=await fixture.snapshot(side),beforeOther=await fixture.snapshot(other);
    await stableUia();const before=await probe(),clock=performance.now();assert.notEqual(before.foreground,hwnd,'Input target must remain in background');
    const result=await call('act',{hwnd,then:'none',steps:[{do:action,target:'Notes '+side,text,check:{target:'Notes '+side,value:expected}}]});
    unchanged(before,await probe(),side+' '+action);assert.equal(result.status,'success',JSON.stringify(result));assert.ok(result.steps.every(s=>s.technique==='background:devtools'&&s.foregroundTaken!==true),JSON.stringify(result));
    const after=await fixture.snapshot(side),untouched=await fixture.snapshot(other);
    assert.equal(after.value,expected,'Independent exact value');assert.equal(untouched.value,beforeOther.value,'Other surface value changed');
    const fresh=after.events.slice(beforeOracle.events.length),foreign=untouched.events.slice(beforeOther.events.length);
    assert.deepEqual(fresh.filter(e=>e.kind==='beforeinput').map(e=>[e.data,e.trusted,e.inputType]),[[text,true,'insertText']],'One exact logical insertion; never infer replay from native input event count');
    assert.ok(fresh.some(e=>e.kind==='input'&&e.trusted&&e.value===expected));assert.deepEqual(foreign.filter(e=>e.kind==='input'||e.kind==='beforeinput'),[],'Other surface received input');
    const measure={side,action,ms:Math.round(performance.now()-clock),value:after.value,otherInputEventsAdded:0,foregroundStable:true,cursorStable:true,events:fresh};measures.push(measure);diagnostic('gesture-proved',measure);
  }
  try{
    await client.rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'edge-split-local-qualification',version:'1'}});
    baseline=await probe();const started=new Date().toISOString();
    identity={executable:browser,state,run:fixture.run,urls:fixture.urls,started};
    const launch=await call('launch',{app:browser,args:`--new-window --window-size=1400,1000 "${fixture.urls[0]}" "${fixture.urls[1]}"`,devtools:true,then:'none'});
    const own=launch.windows?.[0];assert.ok(own&&!own.shared,JSON.stringify(launch));hwnd=own.id;
    bound={hwnd,pid:own.pid,birth:own.processStartedUtc,app:own.app};Object.assign(identity,{hwnd,pid:bound.pid,birth:bound.birth,port:launch.devtoolsPort});
    diagnostic('launched-awaiting-native-split',{bound,port:identity.port,urls:fixture.urls,titles:fixture.titles,baseline,state,instruction:'Prepare Split screen on this exact fixture, then restore baseline. Reads only until two documents/surfaces are proved.'});
    unchanged(baseline,await probe(),'launch');
    const deadline=performance.now()+300000;
    do{
      if(t.signal.aborted)throw new Error('Aborted during Split preparation');
      try{
        const seen=await call('look',{hwnd,show:'both'}),docs=seen.elements.filter(e=>e.role==='document').map(e=>({name:e.name,bounds:e.bounds,visible:e.visible,automationId:e.automationId}));
        const summary=JSON.stringify({docs,ready:[...fixture.ready.keys()]});if(summary!==setupSummary){setupSummary=summary;diagnostic('setup-observation',JSON.parse(summary));}
        if(fixture.ready.size===2&&fixture.titles.every(title=>docs.some(d=>d.name===title&&d.visible))){const actual=await evidence(seen);proof=checkEdgeSplitPrerequisites(actual);diagnostic('prerequisites-proved',{proof,evidence:actual});
          const current=await probe();if(current.foreground===baseline.foreground&&current.foregroundPid===baseline.foregroundPid){break;}diagnostic('await-original-foreground',{current,baseline});proof=null;}
      }catch(error){diagnostic('setup-not-ready',{message:error.message});}
      await new Promise(resolve=>setTimeout(resolve,1000));
    }while(performance.now()<deadline);
    assert.ok(proof,'Real Edge Split prerequisites were not established; no input sent');
    for(const side of ['gauche','droite']){const initial=await fixture.snapshot(side);assert.equal(initial.value,'');assert.deepEqual(initial.events,[]);}
    await gesture('gauche','set','Gauche\nalpha 😀','Gauche\nalpha 😀');
    await gesture('droite','set','Droite beta','Droite beta');
    await gesture('gauche','type',' suite','Gauche\nalpha 😀 suite');
    await gesture('droite','type',' delta','Droite beta delta');
    const sent=client.wire.filter(e=>e.phase==='sent'),acks=client.wire.filter(e=>e.phase==='acknowledged');assert.equal(sent.length,4);assert.equal(new Set(sent.map(e=>e.session+':'+e.id)).size,4);assert.deepEqual(acks.map(e=>e.session+':'+e.id).sort(),sent.map(e=>e.session+':'+e.id).sort());
    diagnostic('qualified',{engine:'edge-split',electronQualified:false,steamQualified:false,proof,measures,wire:client.wire});
  }catch(error){primaryError=error;diagnostic('primary-error',{message:error.message});}finally{
    let cleanupError,closed;diagnostic('cleanup-start',{hwnd});
    // One ordinary WM_CLOSE after independent profile/executable/PID/birth/HWND checks; never force Edge.
    try{if(identity){closed=await ownedEdgeWindow({...identity,mode:'close'});diagnostic('owned-close',closed);assert.ok(closed.closed,'Exact fixture remains open');}}catch(error){cleanupError=error;diagnostic('cleanup-error',{message:error.message});}
    await fixture.close();t.signal.removeEventListener('abort',abort);await client.close();
    if(closed?.closed&&!closed.running)await rm(state,{recursive:true,force:true,maxRetries:0});else diagnostic('state-retained',{state});
    diagnostic('cleanup-finished');if(primaryError&&cleanupError)throw new AggregateError([primaryError,cleanupError],'Edge Split qualification failed and cleanup failed',{cause:primaryError});if(primaryError)throw primaryError;if(cleanupError)throw cleanupError;
  }
});
