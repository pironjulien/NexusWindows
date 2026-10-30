import test from 'node:test';
import assert from 'node:assert/strict';
import {assertRenderedRichText} from './fixtures/claude-cdp-rendered-oracle.mjs';

function observed(){
  const text=['Bonjour','','Julien 😀 suite'],top=283.825,height=24;
  const renderedLines=text.map((value,i)=>({text:value,top:top+i*height,height,breaks:value===''?1:0}));
  return {innerHTML:'Bonjour<div><br></div><div>Julien 😀 suite</div>',innerText:'Bonjour\n\n\nJulien 😀 suite',
    renderedLines,glyphs:renderedLines.flatMap(line=>Array.from(line.text,(value,i)=>({value,left:24+i*12,top:line.top,width:12,height}))),
    breaks:[{value:'<br>',left:24,top:top+height,width:0,height}]};
}
const exact='Bonjour\n\nJulien 😀 suite';
test('rich fixture oracle: raw innerText may differ while exact glyphs and three rendered lines prove two line breaks',()=>{
  const snapshot=observed(),result=assertRenderedRichText(snapshot,exact);
  assert.equal(result.text,exact);assert.equal(result.lineCount,3);
  assert.equal(result.rawInnerText,'Bonjour\n\n\nJulien 😀 suite');
  assert.notEqual(result.rawInnerText,result.text);
  assert.equal(result.rawInnerHTML,snapshot.innerHTML);
});
test('rich fixture oracle rejects real extra/removed lines, changed spaces, glyphs and line spacing',()=>{
  const changes=[
    snapshot=>{snapshot.renderedLines.splice(2,0,{text:'',top:331.825,height:24,breaks:1});},
    snapshot=>{snapshot.renderedLines.splice(1,1);},
    snapshot=>{snapshot.renderedLines[2].text='Julien😀 suite';},
    snapshot=>{snapshot.renderedLines[2].top+=12;},
    snapshot=>{snapshot.glyphs.pop();},
    snapshot=>{snapshot.glyphs.push({...snapshot.glyphs.at(-1)});},
    snapshot=>{snapshot.glyphs.find(glyph=>glyph.value===' ').width=0;},
    snapshot=>{snapshot.breaks=[];},
    snapshot=>{snapshot.breaks.push({value:'<br>',left:24,top:355.825,width:0,height:24});},
  ];
  for(const change of changes){const snapshot=observed();change(snapshot);assert.throws(()=>assertRenderedRichText(snapshot,exact));}
});
test('rich fixture oracle accepts a translated window without relaxing relative line spacing',()=>{
  const snapshot=observed();
  for(const item of [...snapshot.renderedLines,...snapshot.glyphs,...snapshot.breaks])item.top+=200;
  assert.equal(assertRenderedRichText(snapshot,exact).text,exact);
});
