using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class RemoteOfflineTests
{
    [AvaloniaFact]
    public async Task UnreachableHostIsReportedOfflineRatherThanRejected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var view = new RemoteView(new RemoteHost("Offline test", "127.0.0.1", port, "unused", "unused"));
        var sidebar = Assert.IsType<TextBlock>(view.CreateConnectionStatus());
        var offline = typeof(RemoteView).GetField("hostOffline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)typeof(RemoteView).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.True((bool)offline.GetValue(view)!);
        Assert.True(sidebar.IsVisible);
    }
}
