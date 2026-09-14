#!/usr/bin/env node
// Node.js 18+; uses the MCP Streamable HTTP endpoint without extra packages.
const endpoint = 'https://docs-mcp.avaloniaui.net/mcp';
const help = `Avalonia documentation CLI (Node.js 18+)

  npm run avalonia-docs -- search "TreeView data binding" [--max-results 5]
  npm run avalonia-docs -- api "Window.Show"
  npm run avalonia-docs -- rules
  npm run avalonia-docs -- tools
  npm run avalonia-docs -- call TOOL_NAME [JSON_ARGUMENTS]

Add --json to print the MCP result as JSON. Requests time out after 60 seconds.
Direct invocation: node tools/avalonia-docs.mjs <command> ...`;

let sequence = 0;
let session;
let protocol = '2025-03-26';

async function rpc(method, params, notification = false) {
  const id = notification ? undefined : ++sequence;
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json, text/event-stream',
      'MCP-Protocol-Version': protocol,
      ...(session ? { 'Mcp-Session-Id': session } : {}),
    },
    body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
    signal: AbortSignal.timeout(60_000),
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}: ${await response.text()}`);
  session = response.headers.get('mcp-session-id') ?? session;
  if (notification) {
    await response.body?.cancel();
    return;
  }
  let message;
  if (response.headers.get('content-type')?.includes('text/event-stream')) {
    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    let buffer = '';
    try {
      stream: while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += value;
        let boundary;
        while ((boundary = /\r?\n\r?\n/.exec(buffer))) {
          const event = buffer.slice(0, boundary.index);
          buffer = buffer.slice(boundary.index + boundary[0].length);
          const data = event.split(/\r?\n/)
            .filter(line => line.startsWith('data:'))
            .map(line => line.slice(5).replace(/^ /, '')).join('\n');
          if (!data) continue;
          const candidate = JSON.parse(data);
          if (candidate.id === id) {
            message = candidate;
            break stream;
          }
        }
      }
    } finally {
      await reader.cancel();
    }
  } else {
    message = await response.json();
  }
  if (!message || message.id !== id) throw new Error(`Missing MCP response for ${method}`);
  if (message.error) throw new Error(`MCP ${message.error.code}: ${message.error.message}`);
  return message.result;
}

async function main() {
  const args = process.argv.slice(2);
  const json = args.includes('--json');
  if (json) args.splice(args.indexOf('--json'), 1);
  const command = args.shift();
  if (!command || command === '--help' || command === '-h') {
    console.log(help);
    return;
  }
  let name;
  let input = {};
  switch (command) {
    case 'search': {
      name = 'search_avalonia_docs';
      const index = args.indexOf('--max-results');
      if (index !== -1) {
        const count = Number(args[index + 1]);
        if (!Number.isInteger(count) || count < 1 || count > 20) {
          throw new Error('--max-results must be an integer from 1 to 20');
        }
        input.max_results = count;
        args.splice(index, 2);
      }
      if (!args.length || args.some(arg => arg.startsWith('--'))) throw new Error('Provide a search query and valid options');
      input.query = args.join(' ');
      break;
    }
    case 'api':
      name = 'lookup_avalonia_api';
      if (args.length !== 1 || !args[0].trim()) throw new Error('Provide one quoted API name');
      input.type_name = args.shift();
      break;
    case 'rules':
      name = 'get_avalonia_expert_rules';
      if (args.length) throw new Error('rules takes no arguments');
      break;
    case 'tools':
      if (args.length) throw new Error('tools takes no arguments');
      break;
    case 'call':
      if (args.length < 1 || args.length > 2) throw new Error('Usage: call TOOL_NAME [JSON_ARGUMENTS]');
      name = args[0];
      input = JSON.parse(args[1] ?? '{}');
      if (!input || Array.isArray(input) || typeof input !== 'object') throw new Error('Arguments must be a JSON object');
      break;
    default:
      throw new Error(`Unknown command: ${command}. Use --help.`);
  }
  const initialized = await rpc('initialize', {
    protocolVersion: protocol,
    capabilities: {},
    clientInfo: { name: 'avalonia-docs-cli', version: '1.0.0' },
  });
  protocol = initialized.protocolVersion;
  await rpc('notifications/initialized', {}, true);
  let result;
  if (command === 'tools') {
    result = { tools: [] };
    let cursor;
    do {
      const page = await rpc('tools/list', cursor ? { cursor } : {});
      result.tools.push(...page.tools);
      cursor = page.nextCursor;
    } while (cursor);
  } else {
    result = await rpc('tools/call', { name, arguments: input });
  }
  if (json || command === 'tools') console.log(JSON.stringify(result, null, 2));
  else for (const item of result.content ?? []) {
    console.log(item.type === 'text' ? item.text : JSON.stringify(item, null, 2));
  }
  if (result.isError) process.exitCode = 1;
}

main().catch(error => {
  console.error(`avalonia-docs: ${error.message}`);
  process.exitCode = 1;
});
