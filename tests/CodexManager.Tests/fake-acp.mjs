import {createInterface} from 'node:readline';
import {appendFileSync, existsSync, writeFileSync} from 'node:fs';
const recoveryFile = process.argv.find(a=>a.startsWith('--recovery='))?.slice(11);
if (process.argv.includes('--startup-failure') && recoveryFile && !existsSync(recoveryFile)) { writeFileSync(recoveryFile,'startup\n'); process.exit(3); }
const emit = value => process.stdout.write(JSON.stringify(value)+'\n');
const response = (id,result) => emit({jsonrpc:'2.0',id,result});
const update = text => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:{sessionUpdate:'agent_message_chunk',content:{type:'text',text}}}});
let turn;
let permissionTurn;
const configOptions=[{id:'model',name:'Model',type:'select',currentValue:'small',options:[{value:'small',name:'Small'},{value:'large',name:'Large'}]},{id:'reasoning',name:'Thinking',type:'select',currentValue:'low',options:[{value:'low',name:'Low'},{value:'high',name:'High'}]},{id:'fast',name:'Fast mode',type:'boolean',currentValue:false}];
if(process.argv.includes('--access')) configOptions.push({id:'mode',name:'Access',type:'select',currentValue:'ask',options:[{value:'ask',name:'Approve'},{value:'full-access',name:'Full access'}]});
createInterface({input:process.stdin}).on('line',line=>{
 const m=JSON.parse(line);
 switch(m.method){
  case 'initialize': response(m.id,{protocolVersion:1,agentCapabilities:{loadSession:true,sessionCapabilities:{list:{}},promptCapabilities:{image:true,embeddedContext:true}},authMethods:[],...(process.argv.includes('--steering')?{_meta:{steering:{supported:true}}}:{})});break;
  case '_session/steering':
   if(process.argv.includes('--detached')) {response(turn,{stopReason:'end_turn'});turn=null;response(m.id,{outcome:'startedNewTurn'});break;}
   response(m.id,{outcome:turn?'injected':'promptRequired'});break;
  case 'session/new':
   if(process.argv.includes('--commands')) emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:{sessionUpdate:'available_commands_update',availableCommands:[{name:'goal',description:'Set an objective',input:{hint:'objective'}},{name:'compact',description:'Compact history'}]}}});
   response(m.id,{sessionId:'fixture-session',...(process.argv.includes('--config')?{configOptions}:{})});break;
  case 'session/set_config_option':
   configOptions.find(c=>c.id===m.params.configId).currentValue=m.params.value;
   if(m.params.configId==='model'){configOptions[1].currentValue='low';configOptions[1].options=m.params.value==='large'?[{value:'low',name:'Low'},{value:'high',name:'High'}]:[{value:'low',name:'Low'}];}
   response(m.id,{configOptions});break;
  case 'session/list':
   if (!process.argv.includes('--history')) { response(m.id,{sessions:[]}); break; }
   if (!m.params.cursor) { response(m.id,{sessions:[{sessionId:'wrong-folder',cwd:m.params.cwd+'/child',title:'Excluded'}],nextCursor:'page2'}); break; }
   response(m.id,{sessions:[{sessionId:'imported-session',cwd:m.params.cwd,title:'Imported conversation',updatedAt:'2026-09-10T12:00:00Z'},{sessionId:'imported-session',cwd:m.params.cwd,title:'Duplicate'}]});break;
  case 'session/load':
   if(process.argv.includes('--load-many')) {
    for(let i=0;i<450;i++) {
     emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:m.params.sessionId,update:{sessionUpdate:'user_message_chunk',content:{type:'text',text:'Question '+i}}}});
     update('Answer '+i+'\n\n'+('Variable height **markdown** content.\n\n'.repeat(i%9+1)));
    }
    setTimeout(()=>response(m.id,{}),1500); break;
   }
   if(process.argv.includes('--load-hang')) { update('Loading a long history'); break; }
   if(m.params.sessionId==='missing-empty') { emit({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:'Internal error',data:{details:'no rollout found for thread id missing-empty'}}}); break; }
   if(recoveryFile) appendFileSync(recoveryFile,'loaded\n');
   if (m.params.sessionId==='imported-session') {
    emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:m.params.sessionId,update:{sessionUpdate:'user_message_chunk',content:{type:'text',text:'Earlier question'}}}});
    update('Earlier ');update('answer');
   } else update('REPLAY SHOULD NOT DUPLICATE');
   response(m.id,{});break;
  case 'session/prompt':
   if(m.params.prompt[0]?.text==='background-tools') {
    const tool = value => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:value}});
    tool({sessionUpdate:'tool_call',toolCallId:'one',title:'First command',status:'in_progress',rawInput:{command:'echo first'}});
    tool({sessionUpdate:'tool_call',toolCallId:'two',title:'Second command',status:'in_progress',rawInput:{command:'echo second'}});
    update('Work continued');
    tool({sessionUpdate:'tool_call_update',toolCallId:'one',status:'completed'});
    tool({sessionUpdate:'tool_call_update',toolCallId:'two',status:'completed'});
    response(m.id,{stopReason:'end_turn'});break;
   }
   if(m.params.prompt[0]?.text==='stream'){update('First partial answer');setTimeout(()=>{update(' and final answer');response(m.id,{stopReason:'end_turn'});},1200);break;}
   if(process.argv.includes('--commands')) { update(m.params.prompt[0].text); response(m.id,{stopReason:'end_turn'}); break; }
   if(recoveryFile) appendFileSync(recoveryFile,'prompt\n');
   if(m.params.prompt[0]?.text==='disconnect'){update('Partial response');setTimeout(()=>process.exit(3),30);break;}
   if(m.params.prompt[0]?.text==='auth'){response(m.id,{stopReason:'end_turn'});emit({jsonrpc:'2.0',method:'_auth/status_update',params:{authStatus:{kind:'none',label:'Not logged in'}}});break;}
   if(m.params.prompt[0]?.text==='idle-exit'){update('Done');response(m.id,{stopReason:'end_turn'});setTimeout(()=>process.exit(3),150);break;}
   if(m.params.prompt[0]?.text==='hang'){turn=m.id;update('Working');break;}
   if(m.params.prompt[0]?.text==='permission'){permissionTurn=m.id;emit({jsonrpc:'2.0',id:'permission-id',method:'session/request_permission',params:{sessionId:'fixture-session',toolCall:{title:'Test command',toolCallId:'t'},options:[{optionId:'allow',name:'Allow once',kind:'allow_once'},{optionId:'reject',name:'Reject',kind:'reject_once'}]}});break;}
   update('Hello **');update('world**');response(m.id,{stopReason:'end_turn'});break;
  case 'session/cancel': if(turn){response(turn,{stopReason:'cancelled'});turn=null;}break;
  case 'crash': process.exit(3);break;
  case 'echo':response(m.id,m.params);break;
  default: if(m.id==='permission-id'){update(m.result.outcome.optionId??'cancelled');response(permissionTurn,{stopReason:'end_turn'});}
 }
});
