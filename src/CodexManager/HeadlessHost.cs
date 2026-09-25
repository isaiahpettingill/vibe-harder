using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace CodexManager;

public static class HeadlessHost
{
    public static void Run(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        using var store = new Store(backgroundWrites: true); var workspaces = store.Workspaces(); var chats = store.Chats();
        foreach (var chat in chats) chat.RetainHistory = false;
        var runtimes = new Dictionary<string, ChatRuntime>(); SessionService service = null!;
        var backendMaintenance = new BackendUpdates(store, () => workspaces.ToArray(), (owner, provider) =>
            chats.Any(c => c.Provider == provider && runtimes.TryGetValue(c.Id, out var runtime) && runtime.HasBackendProcess && workspaces.Any(w => w.Id == c.WorkspaceId && w.Distro == owner.Distro)));
        ChatRuntime Runtime(Chat chat, Workspace workspace)
        {
            if (!runtimes.TryGetValue(chat.Id, out var runtime))
            {
                runtime = new(chat, workspace, store, AgentProviders.Command(store, workspace, chat.Provider));
                runtime.BackendMaintenance = backendMaintenance;
                runtime.ResolveCommand = () => AgentProviders.Command(store, workspace, chat.Provider);
                runtime.Permission = (request, token) => service.Permission(chat, request, token); runtimes[chat.Id] = runtime;
                runtime.Elicitation = (request, token) => service.Elicit(chat, request, token);
            }
            return runtime;
        }
        service = new(store, workspaces, chats, Runtime);
        if (args.Contains("--pair")) RemoteTrust.OpenPairing(RemoteServer.DirectoryPath);
        Task PairDevice(string device, string code, CancellationToken token, Action cancel)
        {
            if (!RemoteTrust.PairingEnabled(RemoteServer.DirectoryPath)) throw new IOException("Pairing window closed. Restart the headless host with --pair to pair another device.");
            Console.WriteLine($"Pair {device}: {code} (expires in two minutes)"); return Task.CompletedTask;
        }
        string Arg(string key, string fallback) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
        var server = new RemoteServer(RemoteServer.DirectoryPath, Arg("--listen", store.Setting("remoteListenAddress") ?? "0.0.0.0"), int.Parse(Arg("--port", store.Setting("remotePort") ?? "2222")), service.Handle,
            args.Contains("--pair") ? PairDevice
        : null);
        using var stopped = new CancellationTokenSource(); bool stopping = false;
        using var webLifetime = new CancellationTokenSource();
        var backendUpdates = backendMaintenance.Run(webLifetime.Token);
        WebServer? webServer = null;
        async Task StartWeb()
        {
            if (store.Setting("webEnabled") != "1") return;
            try
            {
                var assets = await WebAssets.Ensure(store.DirectoryPath, Console.WriteLine, webLifetime.Token);
                webServer = await WebServer.Start(assets, RemoteServer.DirectoryPath, Arg("--listen", store.Setting("remoteListenAddress") ?? "0.0.0.0"), int.Parse(store.Setting("webPort") ?? "2223"), service.Handle,
                    args.Contains("--pair") ? PairDevice : null, webLifetime.Token);
                Console.WriteLine("Web UI: " + WebAccessStatus.Address(store));
            }
            catch (OperationCanceledException) when (webLifetime.IsCancellationRequested) { }
            catch (Exception error) { Console.Error.WriteLine("Web access: " + error.Message); }
        }
        var webStartup = StartWeb();
        async void Stop()
        {
            if (stopping) return; stopping = true;
            try
            {
                webLifetime.Cancel(); await webStartup; await backendUpdates;
                if (webServer is not null) await webServer.DisposeAsync();
                var shutdowns = runtimes.Values.Select(runtime => runtime.DisposeAsync().AsTask()).ToArray();
                await server.DisposeAsync(); service.Dispose(); await Task.WhenAll(shutdowns);
                foreach (var chat in chats) { store.Save(chat); foreach (var message in chat.Messages) store.SaveMessage(chat, message); }
                await store.FlushAsync();
            }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            finally { stopped.Cancel(); }
        }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Dispatcher.UIThread.Post(Stop); };
        using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; Dispatcher.UIThread.Post(Stop); });
        if (store.Setting("autoResume") == "1")
            foreach (var chat in chats.Where(c => c.InterruptedInput is not null))
                if (workspaces.FirstOrDefault(w => w.Id == chat.WorkspaceId) is { } owner)
                { var input = chat.InterruptedInput!; chat.InterruptedInput = null; _ = Runtime(chat, owner).Send(" ", [], autoResume: true); }
        Dispatcher.UIThread.MainLoop(stopped.Token);
    }
}
