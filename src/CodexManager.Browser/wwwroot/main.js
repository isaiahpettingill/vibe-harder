import { dotnet } from './_framework/dotnet.js';

try {
    const runtime = await dotnet.create();
    runtime.setModuleImports('web', {
        origin: () => location.origin,
        read: key => localStorage.getItem('vibeharder:' + key),
        write: (key, value) => localStorage.setItem('vibeharder:' + key, value),
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
