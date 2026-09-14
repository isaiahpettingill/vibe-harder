import { createInterface } from 'node:readline';
import { readFileSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
const path = process.argv[2];
const emit = value => process.stdout.write(JSON.stringify(value) + '\n');
const result = (id, value) => emit({ jsonrpc: '2.0', id, result: value });
const failure = (id, message) => emit({ jsonrpc: '2.0', id, error: { code: -32602, message } });
const update = (sessionId, message) => emit({ jsonrpc: '2.0', method: 'session/update', params: { sessionId, update: message.role === 'tool'
  ? { sessionUpdate: 'tool_call', toolCallId: message.id, messageId: message.id, title: message.text, status: 'completed' }
  : { sessionUpdate: message.role === 'user' ? 'user_message_chunk' : 'agent_message_chunk', messageId: message.id, content: { type: 'text', text: message.text } } } });
createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line), params = request.params ?? {};
  const sessions = JSON.parse(readFileSync(path, 'utf8'));
  switch (request.method) {
    case 'initialize': result(request.id, { protocolVersion: 1, agentInfo: { name: '@agentclientprotocol/claude-agent-acp', version: '0.76.0' }, agentCapabilities: { loadSession: true, sessionCapabilities: process.argv.includes('--no-fork') ? {} : { fork: {} } } }); break;
    case 'session/new': {
      const sessionId = randomUUID(); sessions[sessionId] = []; writeFileSync(path, JSON.stringify(sessions)); result(request.id, { sessionId }); break;
    }
    case 'session/load':
      if (!sessions[params.sessionId] || process.argv.includes('--reject-load') && params.sessionId !== 'original') { failure(request.id, 'Could not load fork'); break; }
      for (const message of sessions[params.sessionId]) update(params.sessionId, message);
      result(request.id, {}); break;
    case 'session/fork': {
      if (process.argv.includes('--reject-fork')) { failure(request.id, 'Fork rejected'); break; }
      const point = params._meta?.jetbrains?.air?.fork?.messageId;
      const original = sessions[params.sessionId];
      const index = point ? original.findIndex(m => m.id === point) : original.length - 1;
      if (index < 0) { failure(request.id, 'Unknown fork point'); break; }
      const sessionId = randomUUID(); sessions[sessionId] = original.slice(0, process.argv.includes('--ignore-point') ? original.length : index + 1);
      writeFileSync(path, JSON.stringify(sessions)); result(request.id, { sessionId }); break;
    }
    case 'session/prompt': {
      const user = { id: randomUUID(), role: 'user', text: params.prompt.find(p => p.type === 'text')?.text ?? '' };
      const answer = { id: randomUUID(), role: 'assistant', text: 'New answer' };
      sessions[params.sessionId].push(user, answer); writeFileSync(path, JSON.stringify(sessions)); update(params.sessionId, answer); result(request.id, { stopReason: 'end_turn' }); break;
    }
    case 'session/cancel': break;
    default: failure(request.id, 'Unknown method');
  }
});
