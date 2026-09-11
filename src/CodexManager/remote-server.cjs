// App-only SSH endpoint: no shell, PTY, forwarding, SFTP, or system authorized_keys.
const { Server, utils } = require('ssh2');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const readline = require('node:readline');
const directory = process.argv[2];
const address = process.argv[3] || '127.0.0.1';
const port = Number(process.argv[4] || 2222);
fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
const hostFile = path.join(directory, 'host_ed25519');
if (!fs.existsSync(hostFile)) fs.writeFileSync(hostFile, utils.generateKeyPairSync('ed25519').private, { mode: 0o600, flag: 'wx' });
const hostKey = fs.readFileSync(hostFile);
const fingerprint = key => 'SHA256:' + crypto.createHash('sha256').update(key).digest('base64').replace(/=+$/, '');
fs.writeFileSync(path.join(directory, 'host_fingerprint'), fingerprint(utils.parseKey(hostKey).getPublicSSH()) + '\n');
const authorized = () => {
  try { return fs.readFileSync(path.join(directory, 'authorized_keys'), 'utf8').split(/\r?\n/).map(line => utils.parseKey(line.trim())).filter(key => key && !(key instanceof Error) && !Array.isArray(key)); }
  catch { return []; }
};
const channels = new Map(); let sequence = 0;
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
const server = new Server({ hostKeys: [hostKey], ident: 'CodexManager', readyTimeout: 15000 }, client => {
  let clientKey;
  client.on('error', () => {});
  client.on('authentication', context => {
    if (context.method !== 'publickey' || context.username !== 'codex-manager') return context.reject(['publickey']);
    const key = authorized().find(key => key.type === context.key.algo && key.getPublicSSH().equals(context.key.data));
    if (!key || (context.signature && key.verify(context.blob, context.signature, context.hashAlgo) !== true)) return context.reject(['publickey']);
    clientKey = key.getPublicSSH(); context.accept();
  });
  const revocation = setInterval(() => { if (clientKey && !authorized().some(key => key.getPublicSSH().equals(clientKey))) client.end(); }, 5000);
  client.on('close', () => clearInterval(revocation));
  client.on('ready', () => client.on('session', accept => {
    const session = accept();
    session.on('exec', (accept, reject, info) => {
      if (info.command !== 'codex-manager-rpc') return reject();
      const stream = accept(); const channel = String(++sequence); channels.set(channel, stream);
      stream.setEncoding('utf8');
      let buffer = '';
      stream.on('data', data => {
        buffer += data.toString('utf8');
        if (Buffer.byteLength(buffer) > 32 * 1024 * 1024) { stream.close(); return; }
        let end;
        while ((end = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, end); buffer = buffer.slice(end + 1);
          try { const request = JSON.parse(line); send({ channel, request }); } catch { stream.close(); return; }
        }
      });
      stream.on('close', () => channels.delete(channel));
      stream.on('error', () => channels.delete(channel));
    });
  }));
});
server.on('error', error => { process.stderr.write(error.message + '\n'); process.exit(1); });
readline.createInterface({ input: process.stdin }).on('line', line => {
  try { const { channel, response } = JSON.parse(line); channels.get(channel)?.write(JSON.stringify(response) + '\n'); } catch {}
}).on('close', () => process.exit(0));
server.listen(port, address, () => send({ ready: true, fingerprint: fingerprint(utils.parseKey(hostKey).getPublicSSH()) }));
