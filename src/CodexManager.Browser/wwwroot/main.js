import { dotnet } from './_framework/dotnet.js';

try {
    const runtime = await dotnet.create();
    runtime.setModuleImports('web', {
        origin: () => location.origin,
        navigate: url => {
            const target = new URL(url);
            if (target.protocol !== 'https:' || target.username || target.password) throw new Error('Enter an HTTPS host address');
            location.assign(target.href);
        },
        read: key => localStorage.getItem('vibeharder:' + key),
        write: (key, value) => localStorage.setItem('vibeharder:' + key, value),
        downloadUrl: async (url, transferId) => {
            const target = new URL(url, location.origin);
            if (target.origin !== location.origin || !target.pathname.startsWith('/download/')) throw new Error('Invalid download address');
            const response = await fetch(target.href, { cache: 'no-store' });
            if (!response.ok) throw new Error('Download failed (' + response.status + ')');
            const lengthHeader = response.headers.get('Content-Length');
            const length = lengthHeader === null ? -1 : Number(lengthHeader);
            const total = Number.isFinite(length) && length >= 0 ? length : -1;
            const chunks = [];
            let received = 0;
            if (response.body) {
                const reader = response.body.getReader();
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    chunks.push(value);
                    received += value.byteLength;
                    exports.Program.DownloadProgress(transferId, received, total);
                }
            } else {
                const content = await response.arrayBuffer();
                chunks.push(content);
                received = content.byteLength;
                exports.Program.DownloadProgress(transferId, received, total);
            }
            if (total >= 0 && received !== total) throw new Error('Download ended before the file was complete');
            const disposition = response.headers.get('Content-Disposition') ?? '';
            const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
            const quoted = /filename="([^"]+)"/i.exec(disposition);
            const name = encoded ? decodeURIComponent(encoded[1]) : quoted ? quoted[1] : 'download';
            const objectUrl = URL.createObjectURL(new Blob(chunks));
            const link = document.createElement('a');
            link.href = objectUrl; link.download = name; link.click();
            setTimeout(() => URL.revokeObjectURL(objectUrl), 60000);
        },
        download: (name, content) => {
            const url = URL.createObjectURL(new Blob([content]));
            const link = document.createElement('a');
            link.href = url; link.download = name; link.click();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
        }
    });
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    document.addEventListener('visibilitychange', () => exports.Program.VisibilityChanged(!document.hidden));
    window.addEventListener('online', () => exports.Program.VisibilityChanged(true));
    await runtime.runMain(config.mainAssemblyName, [location.href]);
} catch (error) {
    document.getElementById('out').textContent = 'Could not start Vibe Harder. Reload to retry. ' + error.message;
    console.error(error);
}
