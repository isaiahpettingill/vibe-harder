const { spawn } = require('node:child_process');
const { existsSync } = require('node:fs');
const path = require('node:path');
const { createInterface } = require('node:readline');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');

// npx puts the managed packages' node_modules/.bin first on PATH. Use that Codex,
// not the older copy that codex-acp may have installed as a private dependency.
const codex = (process.env.PATH ?? '').split(path.delimiter)
    .filter(bin => path.isAbsolute(bin) && path.basename(bin) === '.bin')
    .map(bin => path.resolve(bin, '../@openai/codex/bin/codex.js'))
    .find(existsSync);
if (!codex) {
    console.error('Could not find the managed Codex package for Ultrafast.');
    process.exit(1);
}
const args = process.argv.slice(2);
const appServer = args.includes('app-server');
if (process.env.VIBE_HARDER_ULTRAFAST_FULL_ACCESS === '1') {
    args.unshift('--disable', 'guardian_approval', '--dangerously-bypass-approvals-and-sandbox');
}
const child = spawn(process.execPath, [codex, ...args], { stdio: appServer ? ['pipe', 'pipe', 'inherit'] : 'inherit' });
child.on('error', error => { console.error(error.message); process.exitCode = 1; });
child.on('close', code => {
    process.exitCode = code ?? 1;
    if (appServer) process.stdin.destroy();
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));

if (appServer) {
    const modelRequests = new Set();
    const supportedModels = new Map();
    function toCodex(packet) {
        if (packet?.method === 'model/list') modelRequests.add(packet.id);
        if (packet?.method === 'turn/start' && supportedModels.get(packet.params?.model) === true) {
            packet.params.serviceTier = 'ultrafast';
            return JSON.stringify(packet);
        }
    }
    function fromCodex(packet) {
        if (!packet || packet.method || !modelRequests.delete(packet.id)) return;
        const models = packet.result?.data;
        if (!Array.isArray(models)) return;
        for (const model of models) {
            const supported = Array.isArray(model?.serviceTiers)
                ? model.serviceTiers.some(tier => tier?.id === 'ultrafast')
                : Array.isArray(model?.additionalSpeedTiers) && model.additionalSpeedTiers.includes('ultrafast');
            if (typeof model?.id === 'string') supportedModels.set(model.id, supported);
            if (typeof model?.model === 'string') supportedModels.set(model.model, supported);
        }
    }
    async function* relay(source, receive) {
        const lines = createInterface({ input: source, crlfDelay: Infinity });
        for await (const line of lines) {
            let packet;
            try { packet = JSON.parse(line); } catch { /* Pass non-JSON through unchanged. */ }
            yield (receive(packet) ?? line) + '\n';
        }
    }
    function stopOnError(error) {
        if (error.code === 'EPIPE' || error.code === 'ERR_STREAM_PREMATURE_CLOSE') return;
        console.error(error.message);
        child.kill();
    }
    pipeline(Readable.from(relay(process.stdin, toCodex)), child.stdin).catch(stopOnError);
    pipeline(Readable.from(relay(child.stdout, fromCodex)), process.stdout).catch(stopOnError);
}
