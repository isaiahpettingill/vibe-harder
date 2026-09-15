using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class RemoteOfflineTests
{
    [AvaloniaFact]
    public async Task UnreachableHostShowsOfflineAndKeepsItWhenAnActionIsAttempted()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var view = new RemoteView(new RemoteHost("Offline test", "127.0.0.1", port, "unused", "unused"));
        var sidebar = Assert.IsType<TextBlock>(view.CreateConnectionStatus());
        await (Task)typeof(RemoteView).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.StartsWith("Offline", sidebar.Text);
        Assert.Contains("127.0.0.1:" + port, sidebar.Text);
        Assert.True(sidebar.IsVisible);
        await (Task)typeof(RemoteView).GetMethod("Call", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [new JsonObject { ["method"] = "list" }])!;
        Assert.StartsWith("Offline", sidebar.Text);
        view.SetConnectionCollapsed(true);
        Assert.DoesNotContain("Offline", sidebar.Text!);
    }
}
