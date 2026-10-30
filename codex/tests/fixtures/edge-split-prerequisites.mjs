import assert from 'node:assert/strict';

// A pure evidence-shape check, never an authorization or a passing live qualification.
// Real launch ownership/account checks and active page/document title proofs remain in the native MCP engine.
export function checkEdgeSplitPrerequisites(evidence){
  const {expected,bound,uia,targets,metrics,windowForTarget,surfaces}=evidence;
  assert.ok(Number.isSafeInteger(bound.hwnd)&&bound.hwnd>0&&Number.isSafeInteger(bound.pid)&&bound.pid>0&&typeof bound.birth==='string'&&bound.birth&&typeof bound.app==='string'&&bound.app,'Exact owned launch identity required');
  assert.equal(uia.window,bound.hwnd);assert.equal(uia.processId,bound.pid);assert.equal(uia.processStartedUtc,bound.birth);assert.equal(uia.app,bound.app);
  assert.equal(expected.urls.length,2);assert.equal(new Set(expected.urls).size,2);assert.equal(new Set(expected.titles).size,2);
  const pages=expected.urls.map(url=>{
    const found=targets.filter(target=>target.type==='page'&&target.url===url);
    assert.equal(found.length,1,'Each local URL needs one true CDP page target');return found[0];
  });
  assert.notEqual(pages[0].id,pages[1].id);
  const windows=pages.map(page=>windowForTarget[page.id]?.windowId);
  assert.ok(windows.every(window=>Number.isSafeInteger(window)&&window>=0),'Browser.getWindowForTarget evidence missing');
  assert.equal(windows[0],windows[1],'The page targets belong to different browser windows');
  const documents=expected.titles.map(title=>{
    const found=uia.elements.filter(node=>node.role==='document'&&node.automationId?.toLowerCase()==='rootwebarea'&&node.name===title&&node.visible&&!node.disabled);
    assert.equal(found.length,1,'Each local title needs one visible UIA root document in the exact HWND');
    assert.ok(Array.isArray(found[0].runtimeId)&&found[0].runtimeId.length>0,'The root document has no UIA runtime identity');return found[0];
  });
  assert.notDeepEqual(documents[0].runtimeId,documents[1].runtimeId);
  const mapping=pages.map((page,i)=>{
    const metric=metrics[page.id],doc=documents[i];
    assert.equal(page.title,expected.titles[i]);assert.equal(metric.href,expected.urls[i]);assert.equal(metric.title,expected.titles[i]);
    assert.ok(metric.innerWidth>0&&metric.innerHeight>0&&Number.isFinite(metric.dpr)&&metric.dpr>0.1&&metric.dpr<16);
    const [left,top,width,height]=doc.bounds,tolerance=Math.ceil(metric.dpr);
    assert.ok([left,top,width,height].every(Number.isFinite)&&width>0&&height>0);
    const matching=surfaces.filter(surface=>surface.root===bound.hwnd&&surface.className==='Chrome_RenderWidgetHostHWND'&&surface.visible&&
      Math.abs(surface.left-left)<=tolerance&&Math.abs(surface.top-top)<=tolerance&&Math.abs(surface.width-width)<=tolerance&&Math.abs(surface.height-height)<=tolerance&&
      Math.abs(surface.width-Math.round(metric.innerWidth*metric.dpr))<=tolerance&&Math.abs(surface.height-Math.round(metric.innerHeight*metric.dpr))<=tolerance);
    assert.equal(matching.length,1,'A root document must match one unique page-sized rendered surface');
    assert.ok(Number.isSafeInteger(matching[0].hwnd)&&matching[0].hwnd>0,'The surface has no native HWND');
    return {targetId:page.id,documentRuntimeId:doc.runtimeId,surface:matching[0].hwnd,bounds:doc.bounds};
  });
  assert.notEqual(mapping[0].surface,mapping[1].surface,'Both documents were assigned the same physical surface');
  const [a,b]=mapping.map(item=>item.bounds);
  assert.ok(a[0]+a[2]<=b[0]||b[0]+b[2]<=a[0]||a[1]+a[3]<=b[1]||b[1]+b[3]<=a[1],'Rendered page documents overlap');
  return {prerequisitesSatisfied:true,liveQualified:false,engine:'edge-split',browserWindowId:windows[0],nativeWindow:bound.hwnd,mapping,
    stillRequired:['native active title binding and cleanup for each page','exact input and independent HTTP state/event oracle','foreground/cursor continuity','owned window/profile cleanup']};
}
