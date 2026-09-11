// Read-only replay probe: logs protocol kinds/counts, never conversation contents.
import {readFileSync} from 'node:fs';
import {spawn, spawnSync} from 'node:child_process';
import {createInterface} from 'node:readline';
const config=JSON.parse(readFileSync(process.argv[2],'utf8'));
const command=config.command.replace(/^npx /,'npx.cmd ');
const child=config.distro
  ? spawn('wsl.exe',['-d',config.distro,'--cd',config.cwd,'--exec','bash','-lc','exec '+config.command],{windowsHide:true})
  : spawn('powershell.exe',['-NoProfile','-NonInteractive','-Command',command],{cwd:config.cwd,windowsHide:true});
child.stderr.resume();
const pending=new Map();let id=0;const counts={};const started=Date.now();
createInterface({input:child.stdout}).on('line',line=>{
  const m=JSON.parse(line);
  if(m.method){const key=m.params?.update?.sessionUpdate??m.method;counts[key]=(counts[key]??0)+1;}
  else if(pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(m.error.message)):p.resolve(m.result);}
});
const request=(method,params)=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:n,method,params})+'\n');});
const timer=setTimeout(()=>{for(const p of pending.values())p.reject(new Error('Replay timed out'));},45000);
try{
  await request('initialize',{protocolVersion:1,clientCapabilities:{fs:{readTextFile:false,writeTextFile:false},terminal:false},clientInfo:{name:'codex-manager-replay-check',version:'1'}});
  await request('session/load',{sessionId:config.session,cwd:config.cwd,mcpServers:[]});
  console.log(JSON.stringify({loaded:true,elapsedMs:Date.now()-started,counts}));
}catch(error){console.log(JSON.stringify({loaded:false,error:error.message,elapsedMs:Date.now()-started,counts}));process.exitCode=1;}
finally{clearTimeout(timer);spawnSync('taskkill',['/PID',String(child.pid),'/T','/F'],{windowsHide:true,stdio:'ignore'});}
