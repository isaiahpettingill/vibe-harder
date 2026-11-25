const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createInterface } = require('node:readline');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');

const bins = (process.env.PATH ?? '').split(path.delimiter)
    .filter(bin => path.isAbsolute(bin) && path.basename(bin) === '.bin');
const managed = entry => bins.map(bin => path.resolve(bin, '../' + entry)).find(fs.existsSync);
const args = process.argv.slice(2);

function run(script, args, env, outbound, inbound, clean = () => {}) {
    if (!script) { console.error('Could not find the managed Codex package.'); process.exitCode = 1; clean(); return; }
    const child = spawn(process.execPath, [script, ...args], { env, stdio: outbound ? ['pipe', 'pipe', 'inherit'] : 'inherit' });
    child.on('error', error => { console.error(error.message); process.exitCode = 1; });
    child.on('close', code => { process.exitCode = code ?? 1; clean(); if (outbound) process.stdin.destroy(); });
    for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
    if (!outbound) return;
    async function* relay(source, receive) {
        for await (const line of createInterface({ input: source, crlfDelay: Infinity })) {
            let packet;
            try { packet = JSON.parse(line); } catch { /* Pass non-JSON through. */ }
            const transformed = receive(packet);
            if (transformed !== false) yield (transformed ?? line) + '\n';
        }
    }
    function stopOnError(error) {
        if (error.code === 'EPIPE' || error.code === 'ERR_STREAM_PREMATURE_CLOSE') return;
        console.error(error.message); child.kill();
    }
    pipeline(Readable.from(relay(process.stdin, outbound)), child.stdin).catch(stopOnError);
    pipeline(Readable.from(relay(child.stdout, inbound)), process.stdout).catch(stopOnError);
}

function appServer() {
    const requests = new Set();
    const models = new Map();
    const directory = process.env.VIBE_HARDER_CODEX_SPEED_STATE;
    run(managed('@openai/codex/bin/codex.js'), args, process.env, packet => {
        if (packet?.method === 'model/list') requests.add(packet.id);
        if (packet?.method !== 'turn/start' || !directory || !models.get(packet.params?.model)) return;
        try {
            const selected = JSON.parse(fs.readFileSync(path.join(directory, 'selected'), 'utf8'));
            if (selected.model !== packet.params.model || selected.sessionId !== packet.params.threadId) return;
        } catch { return; }
        packet.params.serviceTier = 'ultrafast';
        return JSON.stringify(packet);
    }, packet => {
        if (!packet || packet.method || !requests.delete(packet.id) || !Array.isArray(packet.result?.data)) return;
        for (const model of packet.result.data) {
            const supported = model?.serviceTiers?.some?.(tier => tier?.id === 'ultrafast') || model?.additionalSpeedTiers?.includes?.('ultrafast');
            for (const id of [model?.id, model?.model]) if (typeof id === 'string') models.set(id, Boolean(supported));
        }
        if (directory) {
            const file = path.join(directory, 'models');
            const updated = JSON.stringify([...models].filter(([, supported]) => supported).map(([id]) => id));
            fs.writeFileSync(file + '.tmp', updated);
            fs.renameSync(file + '.tmp', file);
        }
    });
}

function adapter() {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'vh-codex-speed-'));
    const selectedFile = path.join(directory, 'selected');
    const wrapper = process.env.VIBE_HARDER_CODEX_WRAPPER;
    const env = { ...process.env, VIBE_HARDER_CODEX_SPEED_STATE: directory,
        CODEX_PATH: wrapper };
    const pending = new Map();
    let model, selected, speed, optionsSnapshot;
    const supports = id => {
        try { return JSON.parse(fs.readFileSync(path.join(directory, 'models'), 'utf8')).includes(id); }
        catch { return false; }
    };
    function select(next) {
        selected = next;
        if (next) fs.writeFileSync(selectedFile, JSON.stringify(next));
        else fs.rmSync(selectedFile, { force: true });
    }
    function update(result) {
        if (!Array.isArray(result?.configOptions)) return;
        model = result.configOptions.find(option => option.category === 'model' || option.id === 'model')?.currentValue;
        if (selected && (selected.model !== model || (result.sessionId && selected.sessionId !== result.sessionId))) select(undefined);
        const index = result.configOptions.findIndex(option => option.id === 'fast-mode');
        speed = result.configOptions[index];
        optionsSnapshot = result.configOptions;
        if (!supports(model)) return;
        const options = [{ value: 'off', name: 'Standard' }];
        if (speed) options.push({ value: 'on', name: 'Fast' });
        const current = speed?.type === 'boolean' ? (speed.currentValue ? 'on' : 'off') : speed?.currentValue ?? 'off';
        const option = { id: 'fast-mode', name: 'Speed', category: 'speed', type: 'select',
            currentValue: selected ? 'ultrafast' : current,
            options: [...options, { value: 'ultrafast', name: 'Ultrafast' }] };
        if (index < 0) result.configOptions.push(option);
        else result.configOptions[index] = option;
        return true;
    }
    run(managed('@agentclientprotocol/codex-acp/dist/index.js'), args.slice(1), env, packet => {
        if (packet?.method !== 'session/set_config_option' || packet.params?.configId !== 'fast-mode' || !supports(model)) return;
        const { value, sessionId } = packet.params;
        if (typeof sessionId !== 'string' || !['off', 'on', 'ultrafast'].includes(value)) return;
        const choice = value === 'ultrafast' ? { model, sessionId } : undefined;
        if (!speed && value !== 'on') {
            select(choice);
            const configOptions = optionsSnapshot.map(option => option.id === 'fast-mode' ? { ...option, currentValue: value } : option);
            process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: packet.id, result: { configOptions } }) + '\n');
            return false;
        }
        if (!speed) return;
        pending.set(packet.id, { choice });
        packet.params.value = speed.type === 'boolean' ? value === 'on' : value === 'on' ? 'on' : 'off';
        if (speed.type === 'boolean') packet.params.type = 'boolean';
        else delete packet.params.type;
        return JSON.stringify(packet);
    }, packet => {
        const change = packet && !packet.method && pending.get(packet.id);
        if (change) {
            pending.delete(packet.id);
            if (packet.result) select(change.choice);
        }
        if (update(packet?.result) || (packet?.method === 'session/update' && update(packet.params?.update))) return JSON.stringify(packet);
    }, () => fs.rmSync(directory, { recursive: true, force: true }));
}

if (args[0] === '--acp') adapter();
else {
    if (process.env.VIBE_HARDER_ULTRAFAST_FULL_ACCESS === '1')
        args.unshift('--disable', 'guardian_approval', '--dangerously-bypass-approvals-and-sandbox');
    if (args.includes('app-server')) appServer();
    else run(managed('@openai/codex/bin/codex.js'), args, process.env);
}
