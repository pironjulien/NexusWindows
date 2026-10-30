import assert from 'node:assert/strict';

// This fixture uses short, unwrapped, left-to-right lines with one common measured line-box height.
// Judge the actual rendered text/blank lines. innerText and innerHTML remain raw diagnostics, not normalized inputs.
export function assertRenderedRichText(snapshot, expected) {
  assert.ok(snapshot&&Array.isArray(snapshot.renderedLines)&&Array.isArray(snapshot.glyphs)&&Array.isArray(snapshot.breaks),'Rendered rich snapshot missing');
  const wanted=expected.split('\n'),lines=snapshot.renderedLines;
  assert.deepEqual(lines.map(line=>line.text),wanted,'Rendered lines differ from the exact requested text');
  assert.ok(lines.length>0&&Number.isFinite(lines[0].top)&&Number.isFinite(lines[0].height)&&lines[0].height>0,'Invalid first rendered line box');
  const first=lines[0];
  for(let i=0;i<lines.length;i++){
    const line=lines[i];
    assert.equal(line.height,first.height,'Fixture line-box heights differ');
    assert.equal(line.top,first.top+i*first.height,'Rendered line spacing differs: extra gap or collapsed blank line');
    const glyphs=snapshot.glyphs.filter(glyph=>glyph.top===line.top);
    assert.equal(glyphs.map(glyph=>glyph.value).join(''),wanted[i],'Rendered glyphs differ, including spaces and Unicode');
    assert.equal(glyphs.length,Array.from(wanted[i]).length,'A glyph was omitted or duplicated');
    for(const glyph of glyphs){
      assert.ok(Number.isFinite(glyph.left)&&Number.isFinite(glyph.width)&&glyph.width>0,'Rendered glyph/space has no positive width');
      assert.equal(glyph.height,line.height,'Rendered glyph line box differs');
    }
    if(wanted[i]==='')assert.ok(snapshot.breaks.some(item=>item.top===line.top&&item.height===line.height),'The requested empty line has no rendered break');
  }
  assert.equal(snapshot.glyphs.length,lines.reduce((sum,line)=>sum+Array.from(line.text).length,0),'A rendered glyph exists outside the expected lines');
  for(const item of snapshot.breaks)assert.ok(lines.some(line=>line.top===item.top&&line.height===item.height),'A rendered break exists outside the expected lines');
  return {text:lines.map(line=>line.text).join('\n'),lineCount:lines.length,lineHeight:first.height,rawInnerText:snapshot.innerText,rawInnerHTML:snapshot.innerHTML};
}
