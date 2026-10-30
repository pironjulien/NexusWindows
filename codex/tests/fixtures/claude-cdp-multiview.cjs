// Account-free, opt-in Electron fixture. The independent oracle is owned by the fixture main process.
const {app,BrowserWindow,WebContentsView,ipcMain}=require('electron');
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const argument=name=>process.argv.find(a=>a.startsWith(`--${name}=`))?.slice(name.length+3);
const run=argument('nexus-cdp-run'),state=argument('nexus-cdp-state'),oracle=new URL(argument('nexus-cdp-oracle'));
if(!run||!state||!path.isAbsolute(state)||oracle.protocol!=='http:'||oracle.hostname!=='127.0.0.1'||oracle.username||oracle.password)throw new Error('A local fixture run identity is required');
const marker=JSON.parse(fs.readFileSync(path.join(state,'.nexus-cdp-fixture.json'),'utf8'));
if(marker.owner!=='nexus-cdp-multiview-fixture'||marker.run!==run)throw new Error('Fixture ownership marker mismatch');
fs.mkdirSync(path.join(state,'electron-profile'),{recursive:true});app.setPath('userData',path.join(state,'electron-profile'));
app.commandLine.appendSwitch('disable-backgrounding-occluded-windows');
let window,control;const views=[];let sequence=0;
function hwnd(){const handle=window.getNativeWindowHandle();return Number(handle.length===8?handle.readBigUInt64LE():handle.readUInt32LE());}
function post(payload){
  if(!window||window.isDestroyed())return;
  const data=JSON.stringify({run,sequence:++sequence,hwnd:hwnd(),...payload});
  const request=http.request(oracle,{method:'POST',headers:{'content-type':'application/json','content-length':Buffer.byteLength(data)}},response=>response.resume());
  request.on('error',()=>{});request.end(data);
}
ipcMain.on('nexus-fixture-oracle',(event,payload)=>{
  const view=views.find(v=>v.view.webContents===event.sender);
  if(view)post({kind:'dom',side:view.side,webContentsId:event.sender.id,event:payload});
});
app.whenReady().then(async()=>{
  app.setAccessibilitySupportEnabled(true);
  window=new BrowserWindow({width:1040,height:840,title:'Nexus CDP multivue',show:false,webPreferences:{contextIsolation:true,nodeIntegration:false,backgroundThrottling:false}});
  const initial=window.loadURL('data:text/html;charset=utf-8,'+encodeURIComponent('<title>Nexus CDP multivue</title><h1>Cadre multivue</h1>'));
  const loads=['gauche','droite'].map(side=>{
    const view=new WebContentsView({webPreferences:{contextIsolation:true,nodeIntegration:false,backgroundThrottling:false,preload:path.join(__dirname,'claude-cdp-multiview-preload.cjs')}});
    views.push({side,view});window.contentView.addChildView(view);
    const html=`<!doctype html><html lang="fr"><meta charset="utf-8"><title>Document ${side}</title><style>body{font:18px system-ui;padding:16px}textarea{width:380px;height:180px}</style><h1>Document ${side}</h1><label for="notes">Notes ${side}</label><textarea id="notes" aria-label="Notes ${side}"></textarea><pre id="oracle"></pre><script>const notes=document.querySelector('#notes'),output=document.querySelector('#oracle'),events=[];for(const kind of ['beforeinput','input','change'])notes.addEventListener(kind,event=>{const record={kind,value:notes.value,trusted:event.isTrusted,inputType:event.inputType??null,data:event.data??null};events.push(record);output.textContent=JSON.stringify(events);window.fixture.emit(record);});window.fixtureSnapshot=()=>({value:notes.value,events:[...events]});</script></html>`;
    return view.webContents.loadURL('data:text/html;charset=utf-8,'+encodeURIComponent(html));
  });
  function layout(){const [width,height]=window.getContentSize(),half=Math.floor(width/2);views[0].view.setBounds({x:0,y:0,width:half,height});views[1].view.setBounds({x:half,y:0,width:width-half,height});}
  window.on('resize',layout);layout();
  control=http.createServer(async(request,response)=>{
    const url=new URL(request.url,'http://127.0.0.1');
    if(request.method!=='GET'||url.pathname!=='/snapshot'||url.searchParams.get('run')!==run){response.writeHead(403).end();return;}
    try{
      const surfaces=await Promise.all(views.map(async v=>({side:v.side,webContentsId:v.view.webContents.id,title:v.view.webContents.getTitle(),bounds:v.view.getBounds(),snapshot:await v.view.webContents.executeJavaScript('window.fixtureSnapshot()')})));
      response.writeHead(200,{'content-type':'application/json'});response.end(JSON.stringify({run,hwnd:hwnd(),surfaces}));
    }catch(error){response.writeHead(500,{'content-type':'application/json'});response.end(JSON.stringify({error:error.message}));}
  });
  await new Promise(resolve=>control.listen(0,'127.0.0.1',resolve));
  await Promise.all([initial,...loads]);window.showInactive();window.moveBottom();
  post({kind:'ready',control:`http://127.0.0.1:${control.address().port}/snapshot?run=${run}`,surfaces:views.map(v=>({side:v.side,webContentsId:v.view.webContents.id,title:v.view.webContents.getTitle(),bounds:v.view.getBounds()}))});
  window.on('closed',()=>{control.closeAllConnections();control.close();app.quit();});
}).catch(error=>{process.stderr.write('Multiview fixture failed: '+error.message+'\n');app.quit();});
app.on('window-all-closed',()=>app.quit());
