const { Client, utils } = require('ssh2');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-headless-'));
fs.mkdirSync(path.join(directory, 'remote'));
const key = utils.generateKeyPairSync('ed25519');
fs.writeFileSync(path.join(directory, 'remote', 'authorized_keys'), key.public);
const port = 22000 + Math.floor(Math.random() * 10000);
const host = spawn(path.resolve(process.argv[2]), ['--headless', '--port', String(port)], { env: { ...process.env, CODEX_MANAGER_DATA: directory }, windowsHide: true, stdio: 'ignore' });
const delay = ms => new Promise(r => setTimeout(r, ms));
(async () => {
  const client = new Client();
  try {
    const file = path.join(directory, 'remote', 'host_ed25519');
    for (let i = 0; i < 100 && !fs.existsSync(file); i++) { if (host.exitCode !== null) throw new Error('Host exited ' + host.exitCode); await delay(100); }
    const serverKey = utils.parseKey(fs.readFileSync(file)).getPublicSSH();
    await delay(400);
    await new Promise((resolve, reject) => client.on('ready', resolve).on('error', reject).connect({ host: '127.0.0.1', port, username: 'codex-manager', privateKey: key.private, hostVerifier: actual => actual.equals(serverKey) }));
    const stream = await new Promise((resolve, reject) => client.exec('codex-manager-rpc', (error, stream) => error ? reject(error) : resolve(stream)));
    let buffer = ''; let pending;
    stream.on('data', chunk => { buffer += chunk; const end = buffer.indexOf('\n'); if (end >= 0) { const reply = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1); pending(reply); } });
    async function request(value) { const reply = new Promise(resolve => pending = resolve); stream.write(JSON.stringify({ id: 'probe', ...value }) + '\n'); const result = await reply; assert.equal(result.error, undefined); return result.result; }
    assert.equal((await request({ method: 'list' })).workspaces.length, 0);
    const workspaceId = await request({ method: 'workspace', path: directory, name: 'Headless probe' });
    await request({ method: 'create', workspaceId, provider: 'Codex' });
    assert.equal((await request({ method: 'list' })).chats.length, 1);
    console.log('Native headless SSH server: authenticated workspace/chat round trip passed.');
  } finally { client.end(); host.kill(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
