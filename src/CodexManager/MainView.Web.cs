using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
#if !MOBILE_CLIENT
    private WebServer? webServer;
    private readonly SemaphoreSlim webConfiguration = new(1, 1);
    private async Task ConfigureWebServer()
    {
        try { await webConfiguration.WaitAsync(discoveryLifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (webServer is not null) { await webServer.DisposeAsync(); webServer = null; }
            if (remoteOnly || closing || store.Setting("webEnabled") != "1") { WebAccessStatus.Set("Web access is off."); return; }
            var assets = await WebAssets.Ensure(store.DirectoryPath, WebAccessStatus.Set, discoveryLifetime.Token);
            WebAccessStatus.Set("Starting web access…");
            var port = int.TryParse(store.Setting("webPort"), out var configured) ? configured : 2223;
            webServer = await WebServer.Start(assets, RemoteServer.DirectoryPath, store.Setting("remoteListenAddress") ?? "0.0.0.0", port, remoteSessions.Handle, ShowPairingCode, discoveryLifetime.Token);
            WebAccessStatus.Set("Ready — " + WebAccessStatus.Address(store));
        }
        catch (Exception error) { WebAccessStatus.Set("Web access: " + error.Message); }
        finally { webConfiguration.Release(); }
    }
#else
    private Task ConfigureWebServer() => Task.CompletedTask;
#endif
}

public static class WebAccessStatus
{
    public static string Text { get; private set; } = "Web access is off.";
    public static event Action? Changed;
    public static void Set(string text) => Dispatcher.UIThread.Post(() => { Text = text; Changed?.Invoke(); });
    public static string Address(Store store) => new UriBuilder("https", Environment.MachineName.ToLowerInvariant(), int.TryParse(store.Setting("webPort"), out var port) ? port : 2223).Uri.AbsoluteUri;
}
