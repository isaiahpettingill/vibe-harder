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
        ChatRuntime Runtime(Chat chat, Workspace workspace)
        {
            if (!runtimes.TryGetValue(chat.Id, out var runtime))
            {
                runtime = new(chat, workspace, store, AgentProviders.Command(store, workspace, chat.Provider));
                runtime.Permission = (request, token) => service.Permission(chat, request, token); runtimes[chat.Id] = runtime;
            }
            return runtime;
        }
        service = new(store, workspaces, chats, Runtime);
        string Arg(string key, string fallback) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
        var server = new RemoteServer(RemoteServer.DirectoryPath, Arg("--listen", store.Setting("remoteListenAddress") ?? "0.0.0.0"), int.Parse(Arg("--port", store.Setting("remotePort") ?? "2222")), service.Handle,
            args.Contains("--pair") ? (device, code, _, _) => { Console.WriteLine($"Pair {device}: {code} (expires in two minutes)"); return Task.CompletedTask; } : null);
        using var stopped = new CancellationTokenSource(); bool stopping = false;
        async void Stop()
        {
            if (stopping) return; stopping = true;
            try
            {
                var shutdowns = runtimes.Values.Select(runtime => runtime.DisposeAsync().AsTask()).ToArray();
                await server.DisposeAsync(); await Task.WhenAll(shutdowns);
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
                { var input = chat.InterruptedInput!; chat.InterruptedInput = null; _ = Runtime(chat, owner).Send("Continue the interrupted request. Inspect saved history and current state; do not repeat completed actions.\n\n" + input.Text, input.Attachments); }
        Dispatcher.UIThread.MainLoop(stopped.Token);
    }
}
