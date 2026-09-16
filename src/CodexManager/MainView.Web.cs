using Avalonia.Threading;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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
    public static string Address(Store store) => Addresses(store)[0];
    public static IReadOnlyList<string> Addresses(Store store)
    {
        var port = int.TryParse(store.Setting("webPort"), out var configured) ? configured : 2223;
        if (IPAddress.TryParse(store.Setting("remoteListenAddress"), out var bind) && !bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any))
            return [new UriBuilder("https", bind.ToString(), port).Uri.AbsoluteUri];
        var addresses = new List<(string Address, int Priority)>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var properties = network.GetIPProperties();
                foreach (var entry in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                {
                    var bytes = entry.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    var tailscale = (network.Name + " " + network.Description).Contains("tailscale", StringComparison.OrdinalIgnoreCase);
                    addresses.Add((entry.Address.ToString(), tailscale ? 0 : properties.GatewayAddresses.Count > 0 ? 1 : 2));
                }
            }
        }
        catch (NetworkInformationException) { }
        return addresses.OrderBy(a => a.Priority).Select(a => new UriBuilder("https", a.Address, port).Uri.AbsoluteUri).Distinct().DefaultIfEmpty(new UriBuilder("https", "127.0.0.1", port).Uri.AbsoluteUri).ToArray();
    }
}
