import {spawn,execFile} from 'node:child_process';
import {once} from 'node:events';
import readline from 'node:readline';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';

export function nativeFixtureClient(exe,env,diagnostic){
  const child=spawn(exe,['--mcp','--agent','claude'],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,...env}});
  let id=0,ended=false,stderr='',buffer='';const pending=new Map(),wire=[];
  const fail=error=>{for(const p of pending.values()){clearTimeout(p.timer);p.reject(error);}pending.clear();};
  child.on('error',e=>{ended=true;fail(e);});child.on('exit',(code,signal)=>{ended=true;diagnostic('mcp-exit',{code,signal});fail(new Error('MCP exited: '+stderr));});child.stdin.on('error',fail);
  child.stderr.on('data',data=>{stderr+=data;buffer+=data;const lines=buffer.split('\n');buffer=lines.pop();for(const line of lines)if(line.startsWith('{"fixture":"claude-cdp-')){diagnostic('native',JSON.parse(line));if(line.startsWith('{"fixture":"claude-cdp-wire"'))wire.push(JSON.parse(line));}});
  const lines=readline.createInterface({input:child.stdout});lines.on('line',line=>{let result;try{result=JSON.parse(line);}catch{return;}const p=pending.get(result.id);if(p){pending.delete(result.id);clearTimeout(p.timer);p.resolve(result);}});
  async function rpc(method,params={},timeout=30000){
    if(ended||child.exitCode!==null)throw new Error('Fixture MCP no longer running');
    const current=++id,wait=new Promise((resolve,reject)=>pending.set(current,{resolve,reject,timer:setTimeout(()=>{pending.delete(current);reject(new Error('MCP timeout '+method+': '+stderr));},timeout)}));
    child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:current,method,params})+'\n');const result=await wait;if(result.error)throw new Error(JSON.stringify(result.error)+'\n'+stderr);return result.result;
  }
  return {wire,rpc,fail,async call(name,args,timeout){const result=await rpc('tools/call',{name,arguments:args},timeout);if(result.isError)throw new Error(name+': '+JSON.stringify(result)+'\n'+stderr);return result.structuredContent??result;},async close(){fail(new Error('Fixture cleanup'));child.stdin.end();if(child.exitCode===null){await Promise.race([once(child,'exit'),new Promise(r=>setTimeout(r,1618))]);if(child.exitCode===null)child.kill();}lines.close();for(const stream of [child.stdin,child.stdout,child.stderr])stream.destroy();}};
}
export async function nativeProbe(exe){return await new Promise((resolve,reject)=>{
  const child=spawn(exe,[],{windowsHide:true,stdio:['pipe','pipe','pipe']});let out='',error='';const timer=setTimeout(()=>{child.kill();reject(new Error('Native probe timeout'));},10000);
  child.stdout.on('data',b=>out+=b);child.stderr.on('data',b=>error+=b);child.stdin.on('error',reject);child.on('error',e=>{clearTimeout(timer);reject(e);});child.on('close',code=>{clearTimeout(timer);try{if(code)throw new Error(error||out);resolve(JSON.parse(out));}catch(e){reject(e);}});child.stdin.end(JSON.stringify({operation:'probe'}));
});}
export async function ownedEdgeWindow(identity){const result=await promisify(execFile)('powershell.exe',['-NoProfile','-NonInteractive','-File',fileURLToPath(new URL('./edge-split-window.ps1',import.meta.url))],{windowsHide:true,timeout:20000,maxBuffer:1024*1024,env:{...process.env,NEXUS_EDGE_SPLIT_IDENTITY:JSON.stringify(identity)}});return JSON.parse(result.stdout);}
export async function cdpRead(url,method,params={}){
  const endpoint=new URL(url);if(endpoint.protocol!=='ws:'||endpoint.hostname!=='127.0.0.1')throw new Error('Read-only CDP needs loopback websocket');
  const socket=new WebSocket(endpoint);let timer;
  try{return await new Promise((resolve,reject)=>{timer=setTimeout(()=>reject(new Error('Read-only CDP timed out: '+method)),5000);socket.onopen=()=>socket.send(JSON.stringify({id:1,method,params}));socket.onerror=()=>reject(new Error('Read-only CDP connection failed'));socket.onclose=()=>reject(new Error('Read-only CDP closed'));socket.onmessage=message=>{const result=JSON.parse(message.data);if(result.id!==1)return;if(result.error)reject(new Error(JSON.stringify(result.error)));else resolve(result.result);};});}finally{clearTimeout(timer);socket.close();}
}
