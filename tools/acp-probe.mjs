import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {resolve} from 'node:path';
import {writeFileSync} from 'node:fs';

const wsl = process.argv.includes('--wsl');
const cwd = wsl ? '/tmp/codex-manager-smoke' : resolve('artifacts/smoke-workspace');
const child = wsl
  ? spawn('wsl.exe', ['-d', 'Debian', '--cd', cwd, '--exec', 'bash', '-lc', 'exec npx -y @agentclientprotocol/codex-acp@1.11.0'], {windowsHide:true})
  : spawn(process.execPath, [resolve('node_modules/@agentclientprotocol/codex-acp/dist/index.js')], {cwd, windowsHide:true});
let seq = 0;
const pending = new Map();
const updates = [];
child.stderr.on('data', b => process.stderr.write(b));
createInterface({input:child.stdout}).on('line', line => {
  try {
    const m=JSON.parse(line);
    if (m.method === 'session/update') { updates.push(m.params.update); const u=m.params.update; if(u.sessionUpdate==='agent_message_chunk') process.stdout.write(u.content.text ?? ''); }
    else if (m.method && m.id !== undefined) child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:m.id,result:{outcome:{outcome:'cancelled'}}})+'\n');
    else if (pending.has(m.id)) { const {resolve,reject}=pending.get(m.id); pending.delete(m.id); m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result); }
  } catch (error) { console.error(error.message); }
});
function request(method, params) { const id=++seq; return new Promise((resolve,reject)=>{pending.set(id,{resolve,reject});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');}); }
const timer=setTimeout(()=>{console.error('Probe timed out');child.kill();process.exit(2)},150000);
try {
  const init=await request('initialize',{protocolVersion:1,clientInfo:{name:'codex-manager-smoke',version:'1'},clientCapabilities:{fs:{readTextFile:false,writeTextFile:false},terminal:false}});
  console.log('INITIALIZED',JSON.stringify(init.agentCapabilities));
  const previous=process.argv.find(a=>a.startsWith('--resume='))?.slice(9);
  let sessionId=previous;
  if(previous) {await request('session/load',{sessionId:previous,cwd,mcpServers:[]}); console.log('RESUMED',previous);}
  else {const created=await request('session/new',{cwd,mcpServers:[]}); sessionId=created.sessionId; console.log('SESSION',sessionId);}
  if(!process.argv.includes('--connect-only')) {
    const prompt=previous?'What was the test word from my previous message? Respond with only that word. Do not use tools.':'Remember the test word ORCHID. Reply with exactly ORCHID. Do not use tools.';
    const result=await request('session/prompt',{sessionId,prompt:[{type:'text',text:prompt}]});
    console.log('\nRESULT',JSON.stringify(result));
  }
  writeFileSync(resolve(`artifacts/acp-${wsl?'wsl':'windows'}.json`),JSON.stringify({sessionId,capabilities:init.agentCapabilities,updates},null,2));
} catch(error){console.error('PROBE FAILED',error.message);process.exitCode=1;}
finally{clearTimeout(timer);child.stdin.end();setTimeout(()=>child.kill(),1000).unref();}
