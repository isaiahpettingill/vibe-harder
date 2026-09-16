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
        downloadUrl: url => {
            const target = new URL(url, location.origin);
            if (target.origin !== location.origin || !target.pathname.startsWith('/download/')) throw new Error('Invalid download address');
            const link = document.createElement('a');
            link.href = target.href; link.download = ''; link.click();
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
