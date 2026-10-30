import {createServer} from 'node:http';
import {once} from 'node:events';
import {randomUUID} from 'node:crypto';

// Fixture only: this module opens localhost HTTP, never a browser, process, profile, window or input route.
export function edgeSplitPage(side,run){
  if(!['gauche','droite'].includes(side)||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(run))throw new Error('Invalid fixture identity');
  return `<!doctype html><html lang="fr"><meta charset="utf-8"><title>Document ${side} ${run}</title><style>body{font:18px system-ui;padding:16px}textarea{width:90%;height:180px;white-space:pre-wrap}</style><h1>Document ${side}</h1><label for="notes">Notes ${side}</label><textarea id="notes" aria-label="Notes ${side}"></textarea><script>
const side=${JSON.stringify(side)},run=${JSON.stringify(run)},notes=document.querySelector('#notes'),events=[];
const post=record=>fetch('/oracle',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({run,side,...record})}).catch(()=>{});
for(const kind of ['beforeinput','input','change'])notes.addEventListener(kind,event=>{const record={kind,value:notes.value,trusted:event.isTrusted,inputType:event.inputType??null,data:event.data??null};events.push(record);post({kind:'event',event:record});});
const channel=new EventSource('/commands/'+side+'?run='+run);
channel.addEventListener('message',message=>{const request=JSON.parse(message.data);if(request.op==='snapshot')post({kind:'snapshot',request:request.id,value:notes.value,events:[...events],title:document.title,href:location.href});});
channel.addEventListener('open',()=>post({kind:'ready',title:document.title,href:location.href}));
</script></html>`;
}
export async function createEdgeSplitFixture(){
  const run=randomUUID(),sides=['gauche','droite'],channels=new Map(),pending=new Map(),ready=new Map(),events=[];
  let base,closed=false;
  const server=createServer(async(request,response)=>{
    try{
      const url=new URL(request.url,'http://127.0.0.1');
      const side=sides.find(side=>url.pathname==='/'+side);
      if(request.method==='GET'&&side&&url.searchParams.get('run')===run){response.writeHead(200,{'content-type':'text/html;charset=utf-8','content-security-policy':"default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'"});response.end(edgeSplitPage(side,run));return;}
      const commandSide=sides.find(side=>url.pathname==='/commands/'+side);
      if(request.method==='GET'&&commandSide&&url.searchParams.get('run')===run){
        response.writeHead(200,{'content-type':'text/event-stream','cache-control':'no-store','connection':'keep-alive'});response.write(': fixture channel\n\n');
        channels.get(commandSide)?.end();channels.set(commandSide,response);
        response.on('close',()=>{if(channels.get(commandSide)===response)channels.delete(commandSide);});return;
      }
      if(request.method==='POST'&&url.pathname==='/oracle'){
        let body='';for await(const part of request){body+=part;if(body.length>1024*1024)throw new Error('Fixture oracle too large');}
        const record=JSON.parse(body);if(record.run!==run||!sides.includes(record.side)){response.writeHead(403).end();return;}
        if(record.kind==='ready'){
          if(record.href!==base+'/'+record.side+'?run='+run||record.title!=='Document '+record.side+' '+run){response.writeHead(400).end();return;}
          ready.set(record.side,record);
        }else if(record.kind==='snapshot'){
          const waiter=pending.get(record.request);if(waiter&&waiter.side===record.side){pending.delete(record.request);clearTimeout(waiter.timer);waiter.resolve(record);}
        }else if(record.kind==='event')events.push(record);
        response.writeHead(204).end();return;
      }
      response.writeHead(404).end();
    }catch{response.destroy();}
  });
  server.on('clientError',(_error,socket)=>socket.destroy());server.listen(0,'127.0.0.1');await once(server,'listening');base='http://127.0.0.1:'+server.address().port;
  return {
    run,urls:sides.map(side=>base+'/'+side+'?run='+run),titles:sides.map(side=>'Document '+side+' '+run),ready,events,
    snapshot(side){
      if(closed||!channels.has(side)||!ready.has(side))return Promise.reject(new Error('That exact fixture page is not ready'));
      const id=randomUUID();const wait=new Promise((resolve,reject)=>pending.set(id,{side,resolve,reject,timer:setTimeout(()=>{pending.delete(id);reject(new Error('Independent fixture snapshot timed out'));},5000)}));
      channels.get(side).write('data: '+JSON.stringify({op:'snapshot',id})+'\n\n');return wait;
    },
    async close(){closed=true;for(const waiter of pending.values()){clearTimeout(waiter.timer);waiter.reject(new Error('Fixture closed'));}pending.clear();for(const channel of channels.values())channel.end();channels.clear();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));},
  };
}
