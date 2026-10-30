import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {edgeSplitPage} from './fixtures/edge-split-fixture.mjs';
import {checkEdgeSplitPrerequisites} from './fixtures/edge-split-prerequisites.mjs';

function receipt(){
  const urls=['http://127.0.0.1:1234/gauche','http://127.0.0.1:1234/droite'],titles=['Document gauche','Document droite'];
  const bound={hwnd:100,pid:50,birth:'fixture-birth',app:'fixture-edge'};
  return {expected:{urls,titles},bound,uia:{window:100,processId:50,processStartedUtc:'fixture-birth',app:'fixture-edge',elements:titles.map((name,i)=>({name,role:'document',automationId:'rootWebArea',runtimeId:[42,i],visible:true,disabled:false,bounds:[i*400,100,400,300]}))},
    targets:urls.map((url,i)=>({id:'page'+i,type:'page',url,title:titles[i]})),windowForTarget:{page0:{windowId:8},page1:{windowId:8}},
    metrics:{page0:{href:urls[0],title:titles[0],innerWidth:320,innerHeight:240,dpr:1.25},page1:{href:urls[1],title:titles[1],innerWidth:320,innerHeight:240,dpr:1.25}},
    surfaces:[0,1].map(i=>({root:100,hwnd:200+i,className:'Chrome_RenderWidgetHostHWND',visible:true,left:i*400,top:100,width:400,height:300}))};
}
test('Edge Split shape gate does not turn consistent candidate metadata into a live qualification',()=>{
  const result=checkEdgeSplitPrerequisites(receipt());assert.equal(result.prerequisitesSatisfied,true);assert.equal(result.liveQualified,false);assert.equal(result.mapping.length,2);
});
test('Edge Split shape gate refuses missing pages, different windows, recycled process and ambiguous/mismatched surfaces',()=>{
  const changes=[
    evidence=>{evidence.targets[1].type='iframe';},
    evidence=>{evidence.windowForTarget.page1.windowId=9;},
    evidence=>{evidence.uia.processStartedUtc='another-birth';},
    evidence=>{evidence.uia.window=101;},
    evidence=>{evidence.uia.elements[1].runtimeId=[42,0];},
    evidence=>{evidence.surfaces.push({...evidence.surfaces[0],hwnd:999});},
    evidence=>{evidence.surfaces[1].width=600;},
    evidence=>{evidence.surfaces[1].root=101;},
    evidence=>{evidence.targets[1].url='http://127.0.0.1:1234/other';},
  ];
  for(const change of changes){const evidence=receipt();change(evidence);assert.throws(()=>checkEdgeSplitPrerequisites(evidence));}
});
test('Dedicated Edge pages are standalone local HTML, with no Electron bridge or input-producing snapshot command',()=>{
  const run='01234567-89ab-cdef-0123-456789abcdef';
  for(const side of ['gauche','droite']){
    const html=edgeSplitPage(side,run);assert.ok(html.includes('Notes '+side));assert.ok(!html.includes('electron'));
    new vm.Script(html.split('<script>')[1].split('</script>')[0]);
    assert.ok(!/\.focus\(|\.click\(|dispatchEvent\(|execCommand\(/.test(html));
  }
});
