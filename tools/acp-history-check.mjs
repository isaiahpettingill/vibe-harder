// Read-only handshake/history check. Does not create sessions or send prompts.
import {spawn, spawnSync} from 'node:child_process';
import {createInterface} from 'node:readline';
const provider = process.argv[2] ?? 'codex';
const commands = {codex:'npx -y @agentclientprotocol/codex-acp@1.13.0', claude:'npx -y @agentclientprotocol/claude-agent-acp@0.76.0', opencode:'opencode acp'};
if (!commands[provider]) throw new Error('Use codex, claude, or opencode');
const cwd = process.argv[3] ?? process.cwd();
const distro = process.argv[4];
const child = distro
  ? spawn('wsl.exe',['-d',distro,'--cd',cwd,'--exec','bash','-lc','exec '+commands[provider]],{windowsHide:true})
  : process.platform === 'win32'
    ? spawn('powershell.exe',['-NoProfile','-NonInteractive','-Command',commands[provider]],{cwd,windowsHide:true})
    : spawn('/bin/sh',['-lc','exec '+commands[provider]],{cwd});
let id=0;
const pending=new Map();
child.stderr.resume();
createInterface({input:child.stdout}).on('line',line=>{
  let m; try {m=JSON.parse(line);} catch {return;}
  if(pending.has(m.id)) {const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(m.error.message)):p.resolve(m.result);}
});
child.on('exit',code=>{for(const p of pending.values())p.reject(new Error('Adapter exited: '+code));});
child.on('error',error=>{for(const p of pending.values())p.reject(error);});
const request=(method,params)=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:n,method,params})+'\n');});
const timeout=setTimeout(()=>{for(const p of pending.values())p.reject(new Error('History check timed out'));},90000);
try {
  const init=await request('initialize',{protocolVersion:1,clientInfo:{name:'codex-manager-history-check',version:'1'},clientCapabilities:{fs:{readTextFile:false,writeTextFile:false},terminal:false}});
  let count=0,pages=0,cursor=null;const cursors=new Set();
  do {
    const page=await request('session/list',{cwd,cursor});count+=page.sessions.length;pages++;
    cursor=page.nextCursor;
    if(cursor&&cursors.has(cursor))throw new Error('Repeated cursor');
    cursors.add(cursor);
  } while(cursor);
  console.log(JSON.stringify({provider,host:distro??'local',version:init.agentInfo?.version,loadSession:init.agentCapabilities?.loadSession,count,pages}));
} catch(error) {console.error(provider+': '+error.message);process.exitCode=1;}
finally {
  clearTimeout(timeout);child.stdin.end();
  if(process.platform==='win32')spawnSync('taskkill',['/PID',String(child.pid),'/T','/F'],{windowsHide:true,stdio:'ignore'});
  else child.kill();
}
