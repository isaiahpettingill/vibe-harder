import {createInterface} from 'node:readline';
import {appendFileSync, existsSync, writeFileSync} from 'node:fs';
const recoveryFile = process.argv.find(a=>a.startsWith('--recovery='))?.slice(11);
const startupLog = process.argv.find(a=>a.startsWith('--startup-log='))?.slice(14);
if (startupLog) appendFileSync(startupLog, 'started\n');
if (process.argv.includes('--startup-failure') && recoveryFile && !existsSync(recoveryFile)) { writeFileSync(recoveryFile,'startup\n'); process.exit(3); }
const emit = value => process.stdout.write(JSON.stringify(value)+'\n');
const response = (id,result) => emit({jsonrpc:'2.0',id,result});
let activeSession = 'fixture-session';
const update = text => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:activeSession,update:{sessionUpdate:'agent_message_chunk',content:{type:'text',text}}}});
let turn;
let backgroundTask=null;
let permissionTurn;
let questionTurn;
let clientCapabilities;
const configOptions=[{id:'model',name:'Model',type:'select',currentValue:'small',options:[{value:'small',name:'Small'},{value:'large',name:'Large'}]},{id:'reasoning',name:'Thinking',type:'select',currentValue:'low',options:[{value:'low',name:'Low'},{value:'high',name:'High'}]},{id:'fast',name:'Fast mode',type:'boolean',currentValue:false}];
if(process.argv.includes('--dirac-defaults')) configOptions.push(...['yolo','auto_approve'].map(id=>({id,name:id,type:'boolean',currentValue:false})));
if(process.argv.includes('--access')) configOptions.push({id:'mode',name:'Access',type:'select',currentValue:'ask',options:[{value:'ask',name:'Approve'},{value:'full-access',name:'Full access'}]});
if(process.argv.includes('--claude-access')) configOptions.push({id:'mode',name:'Mode',type:'select',currentValue:'default',options:[{value:'default',name:'Manual'},{value:'bypassPermissions',name:'Bypass permissions'}]});
createInterface({input:process.stdin}).on('line',line=>{
 const m=JSON.parse(line);
 const promptLog=process.argv.find(a=>a.startsWith('--prompt-log='))?.slice(13);
 if(m.method==='session/prompt' && promptLog) appendFileSync(promptLog,JSON.stringify(m.params.prompt)+'\n');
 switch(m.method){
  case 'initialize': clientCapabilities=m.params.clientCapabilities;response(m.id,{protocolVersion:1,agentCapabilities:{...(process.argv.includes("--dirac")?{_meta:{"dev.dirac/whisper":true,"dev.dirac/steering_status":true,"dev.dirac/checkpoints.list":true,"dev.dirac/checkpoints.restore":true}}:{}),loadSession:true,sessionCapabilities:{list:{}},promptCapabilities:{image:true,embeddedContext:true}},authMethods:[],...(process.argv.includes('--steering')?{_meta:{steering:{supported:true}}}:{})});break;
  case 'fixture/capabilities': response(m.id,clientCapabilities);break;
  case '_dev.dirac/whisper':
   if(turn) emit({jsonrpc:'2.0',method:'_dev.dirac/steering_status',params:{sessionId:m.params.sessionId,steeringMessageId:'s1',status:'queued'}});break;
  case '_dev.dirac/checkpoints.list': response(m.id,{checkpoints:[{id:'checkpoint-1',createdAt:'2026-09-15T12:00:00Z',commitHash:'123456789',messageId:'message-1'}]});break;
  case '_dev.dirac/checkpoints.restore':
   if(m.params.checkpointId!=='checkpoint-1') throw new Error('Unknown checkpoint');
   response(m.id,{});break;
  case '_session/steering':
   if(process.argv.includes('--detached')) {response(turn,{stopReason:'end_turn'});turn=null;response(m.id,{outcome:'startedNewTurn'});break;}
   response(m.id,{outcome:turn?'injected':'promptRequired'});break;
  case 'session/new':
   if(process.argv.includes('--commands')) emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:{sessionUpdate:'available_commands_update',availableCommands:[{name:'goal',description:'Set an objective',input:{hint:'objective'}},{name:'compact',description:'Compact history'}]}}});
   response(m.id,{sessionId:'fixture-session',...(process.argv.includes('--config')?{configOptions}:{})});break;
  case 'session/set_config_option':
   configOptions.find(c=>c.id===m.params.configId).currentValue=m.params.value;
   if(m.params.configId==='model'){configOptions[1].currentValue='low';configOptions[1].options=m.params.value==='large'?[{value:'low',name:'Low'},{value:'high',name:'High'}]:[{value:'low',name:'Low'}];}
   if(process.argv.includes('--reset-access') && m.params.configId !== 'mode') configOptions.find(c=>c.id==='mode').currentValue='ask';
   response(m.id,{configOptions});break;
  case 'session/list':
   if (!process.argv.includes('--history')) { response(m.id,{sessions:[]}); break; }
   if (!m.params.cursor) { response(m.id,{sessions:[{sessionId:'wrong-folder',cwd:m.params.cwd+'/child',title:'Excluded'}],nextCursor:'page2'}); break; }
   response(m.id,{sessions:[{sessionId:'imported-session',cwd:m.params.cwd,title:'Imported conversation',updatedAt:'2026-09-10T12:00:00Z'},{sessionId:'imported-session',cwd:m.params.cwd,title:'Duplicate'}]});break;
  case 'session/load':
   activeSession = m.params.sessionId;
   if(process.argv.includes('--load-many')) {
    for(let i=0;i<450;i++) {
     emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:m.params.sessionId,update:{sessionUpdate:'user_message_chunk',content:{type:'text',text:'Question '+i}}}});
     update('Answer '+i+'\n\n'+('Variable height **markdown** content.\n\n'.repeat(i%9+1)));
    }
    setTimeout(()=>response(m.id,{}),1500); break;
   }
   if(process.argv.includes('--load-hang')) { update('Loading a long history'); break; }
   if(m.params.sessionId==='missing-empty') { emit({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:'Internal error',data:{details:'no rollout found for thread id missing-empty'}}}); break; }
   if(m.params.sessionId==='missing-short') { emit({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:'Internal error',data:{details:'no rollout'}}}); break; }
   if(recoveryFile) appendFileSync(recoveryFile,'loaded\n');
   if (m.params.sessionId==='imported-session') {
    emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:m.params.sessionId,update:{sessionUpdate:'user_message_chunk',content:{type:'text',text:'Earlier question'}}}});
    update('Earlier ');update('answer');
   } else update('REPLAY SHOULD NOT DUPLICATE');
   response(m.id,process.argv.includes('--config')?{configOptions}:{});break;
  case 'session/prompt':
   if(process.argv.includes('--expired-auth')) { emit({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:'Provider request failed',data:{error:{code:'invalid_grant',message:'Refresh token expired'}}}}); break; }
   if(process.argv.includes('--silent-turn')) { response(m.id,{stopReason:'end_turn'}); break; }
   if(m.params.prompt[0]?.text==='subagents') {
    const child = (sessionId, value) => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId,update:value}});
    child('fixture-session',{sessionUpdate:'subagent_spawned',subagentSessionId:'child-1',name:'Investigate',task:'Check the implementation'});
    child('child-1',{sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Child answer'}});
    child('child-1',{sessionUpdate:'tool_call',toolCallId:'read',title:'Read file',status:'in_progress',content:[{type:'content',content:{type:'text',text:'File contents'}}]});
    child('child-1',{sessionUpdate:'tool_call_update',toolCallId:'read',status:'completed'});
    child('child-1',{sessionUpdate:'subagent_update',subagentSessionId:'child-2',name:'Review',task:'Review findings',state:'running'});
    child('child-2',{sessionUpdate:'agent_thought_chunk',content:{type:'text',text:'Nested reasoning'}});
    child('child-1',{sessionUpdate:'subagent_state_update',subagentSessionId:'child-2',state:'completed'});
    child('fixture-session',{sessionUpdate:'subagent_update',subagentSessionId:'child-1',state:'completed'});
    child('unknown-session',{sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Must not leak'}});
    update('Parent answer');response(m.id,{stopReason:'end_turn'});break;
   }
   if(m.params.prompt[0]?.text==='plan') {
    const plan = status => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:{sessionUpdate:'plan',entries:[{content:'Answer the user',status,priority:'medium'}]}}});
    plan('pending');plan('in_progress');update('Hello **');plan('in_progress');update('world**');plan('completed');response(m.id,{stopReason:'end_turn'});break;
   }
   const onlineFile = process.argv.find(a=>a.startsWith('--network-outage='))?.slice(17);
   if(onlineFile && !existsSync(onlineFile)) { emit({jsonrpc:'2.0',id:m.id,error:{code:-32603,message:'stream disconnected: connection reset by peer'}}); break; }
   if(m.params.prompt[0]?.text==='background-tools') {
    const tool = value => emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:'fixture-session',update:value}});
    tool({sessionUpdate:'tool_call',toolCallId:'one',title:'First command',status:'in_progress',rawInput:{command:'echo first'},content:[{type:'content',content:{type:'text',text:'first result'}}]});
    tool({sessionUpdate:'tool_call',toolCallId:'two',title:'Second command',status:'in_progress',rawInput:{command:'echo second'}});
    update('Work continued');
    tool({sessionUpdate:'tool_call_update',toolCallId:'one',status:'completed'});
    tool({sessionUpdate:'tool_call_update',toolCallId:'two',status:'completed',rawOutput:{stdout:'second result',exitCode:0}});
    response(m.id,{stopReason:'end_turn'});break;
   }
   if(m.params.prompt[0]?.text==='stream'){update('First partial answer');setTimeout(()=>{update(' and final answer');response(m.id,{stopReason:'end_turn'});},1200);break;}
   if(process.argv.includes('--commands')) { update(m.params.prompt[0].text); response(m.id,{stopReason:'end_turn'}); break; }
   if(recoveryFile) appendFileSync(recoveryFile,'prompt\n');
   if(m.params.prompt[0]?.text==='disconnect'){update('Partial response');setTimeout(()=>process.exit(3),30);break;}
   if(m.params.prompt[0]?.text==='auth'){response(m.id,{stopReason:'end_turn'});emit({jsonrpc:'2.0',method:'_auth/status_update',params:{authStatus:{kind:'none',label:'Not logged in'}}});break;}
   if(m.params.prompt[0]?.text==='idle-exit'){update('Done');response(m.id,{stopReason:'end_turn'});setTimeout(()=>process.exit(3),150);break;}
   if(m.params.prompt[0]?.text?.startsWith('async-question:')){const rollout=m.params.prompt[0].text.slice(15);const id=m.id;update('Checking');setTimeout(()=>{const call=(name,args,call_id)=>appendFileSync(rollout,JSON.stringify({timestamp:new Date().toISOString(),type:'response_item',payload:{type:'function_call',name,arguments:JSON.stringify(args),call_id}})+'\n');call('send_message_to_user_async',{message:'Heads up: **staging** is down.'},'call_note');call('request_user_input_async',{questions:[{title:'Which rates apply?',options:['1x / 1x / 1x','Ask the manager']},{title:'Anything else?'}]},'call_ask');setTimeout(()=>{update(' done');response(id,{stopReason:'end_turn'});},300);},600);break;}
   if(m.params.prompt[0]?.text?.startsWith('<send_user_message_question_reply>')){update('Got it');response(m.id,{stopReason:'end_turn'});break;}
   if(['background','background-long','workflow'].includes(m.params.prompt[0]?.text)){const kind=m.params.prompt[0].text;const send=u=>emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:activeSession,update:u}});const air={jetbrains:{air:{version:1}}};
     if(kind!=='workflow'){send({sessionUpdate:'tool_call',toolCallId:'bash-1',title:'npm run dev',kind:'execute',status:'in_progress',rawInput:{command:'npm run dev',description:'Start the dev server'},_meta:{jetbrains:{air:{version:1,commandTitle:'Start the dev server'}}}});
       send({sessionUpdate:'tool_call_update',toolCallId:'bash-1',status:'completed',_meta:{jetbrains:{air:{version:1,asyncTasks:{backgrounded:true}}}}});}
     send({sessionUpdate:'async_task_spawned',asyncTaskId:'task-1',name:kind==='workflow'?'Nightly workflow':'npm run dev',taskType:kind==='workflow'?'workflow':'shell',description:kind==='workflow'?'Runs the nightly checks':'Start the dev server',showInTranscript:true,canStop:true,...(kind==='workflow'?{}:{toolCallId:'bash-1'})});
     send({sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Started it in the background.'}});response(m.id,{stopReason:'end_turn'});
     backgroundTask=kind;if(kind!=='background-long')setTimeout(()=>{send({sessionUpdate:'async_task_progress',asyncTaskId:'task-1',summary:'Compiled 12 files'});
       setTimeout(()=>{send({sessionUpdate:'async_task_state_update',asyncTaskId:'task-1',state:'completed',summary:'Server ready on :3000'});backgroundTask=null;
         // The agent wakes up on its own after the task ends: an autonomous cycle outside any prompt.
         setTimeout(()=>{send({sessionUpdate:'agent_message_chunk',content:{type:'text',text:'The dev server is up on port 3000.'}});},300);},300);},2500);break;}
   if(m.params.prompt[0]?.text==='hang'){turn=m.id;update('Working');break;}
   if(m.params.prompt[0]?.text==='permission'){permissionTurn=m.id;emit({jsonrpc:'2.0',id:'permission-id',method:'session/request_permission',params:{sessionId:'fixture-session',toolCall:{title:'Test command',toolCallId:'t'},options:[{optionId:'allow',name:'Allow once',kind:'allow_once'},{optionId:'reject',name:'Reject',kind:'reject_once'}]}});break;}
   if(m.params.prompt[0]?.text==='question'){questionTurn=m.id;emit({jsonrpc:'2.0',id:'question-id',method:'elicitation/create',params:{sessionId:'fixture-session',mode:'form',message:'Which approach?',requestedSchema:{type:'object',properties:{approach:{type:'string',oneOf:[{const:'simple',title:'Simple'},{const:'broad',title:'Broad'}]},note:{type:'string',title:'Additional note'}},required:['approach']}}});break;}
   update('Hello **');update('world**');response(m.id,{stopReason:'end_turn'});break;
  case '_session/async_task/stop': if(backgroundTask&&m.params.asyncTaskId==='task-1'){backgroundTask=null;emit({jsonrpc:'2.0',method:'session/update',params:{sessionId:activeSession,update:{sessionUpdate:'async_task_state_update',asyncTaskId:'task-1',state:'stopped'}}});response(m.id,{stopped:true});}else response(m.id,{stopped:false});break;
  case 'session/cancel': if(turn){response(turn,{stopReason:'cancelled'});turn=null;}break;
  case 'crash': process.exit(3);break;
  case 'echo':response(m.id,m.params);break;
  default: if(m.id==='permission-id'){update(m.result.outcome.optionId??'cancelled');response(permissionTurn,{stopReason:'end_turn'});}
   else if(m.id==='question-id'){update(JSON.stringify(m.result));response(questionTurn,{stopReason:'end_turn'});}
 }
});
