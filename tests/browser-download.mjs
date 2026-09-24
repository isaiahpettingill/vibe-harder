import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

let imports;
const progress = [];
const clicks = [];
let savedBlob;
globalThis.__testDotnet = {
    create: async () => ({
        setModuleImports: (_, module) => { imports = module; },
        getConfig: () => ({ mainAssemblyName: 'Test' }),
        getAssemblyExports: async () => ({ Program: {
            VisibilityChanged: () => {},
            DownloadProgress: (...values) => progress.push(values)
        } }),
        runMain: async () => {}
    })
};
globalThis.location = { origin: 'https://local.test' };
globalThis.document = {
    addEventListener: () => {},
    createElement: () => ({ click() { clicks.push([this.href, this.download]); } })
};
globalThis.window = { addEventListener: () => {} };
globalThis.fetch = async () => new Response(new ReadableStream({
    start(controller) {
        controller.enqueue(new Uint8Array([1, 2]));
        controller.enqueue(new Uint8Array([3, 4, 5]));
        controller.close();
    }
}), { headers: { 'Content-Length': '5', 'Content-Disposition': 'attachment; filename="report.pdf"' } });
URL.createObjectURL = blob => { savedBlob = blob; return 'blob:download'; };
URL.revokeObjectURL = () => {};
globalThis.setTimeout = () => 0;

const source = (await readFile(new URL('../src/CodexManager.Browser/wwwroot/main.js', import.meta.url), 'utf8'))
    .replace("import { dotnet } from './_framework/dotnet.js';", 'const dotnet = globalThis.__testDotnet;');
await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
await imports.downloadUrl('/download/ticket', 7);
assert.deepEqual(progress, [[7, 2, 5], [7, 5, 5]]);
assert.equal(savedBlob.size, 5);
assert.deepEqual(clicks, [['blob:download', 'report.pdf']]);
await assert.rejects(imports.downloadUrl('https://other.test/download/ticket', 8), /Invalid download address/);
