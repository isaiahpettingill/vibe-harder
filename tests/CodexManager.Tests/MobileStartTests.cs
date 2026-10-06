using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class MobileStartTests
{
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(predicate());
    }
    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);
    private static JsonObject Row(string id, string workspace, DateTimeOffset updated) => new() { ["id"] = id, ["workspaceId"] = workspace, ["title"] = id, ["archived"] = false, ["updated"] = updated.ToString("O") };
    private static JsonObject Chat() => new()
    {
        ["provider"] = "Codex", ["busy"] = false, ["messages"] = new JsonArray(), ["permissions"] = new JsonArray(),
        ["config"] = new JsonArray(
            new JsonObject { ["id"] = "model", ["name"] = "Model", ["current"] = "large", ["values"] = new JsonArray(new JsonObject { ["value"] = "small", ["name"] = "Small" }, new JsonObject { ["value"] = "large", ["name"] = "Large" }) },
            new JsonObject { ["id"] = "mode", ["name"] = "Mode", ["current"] = "ask", ["values"] = new JsonArray(new JsonObject { ["value"] = "ask", ["name"] = "Ask" }, new JsonObject { ["value"] = "auto", ["name"] = "Auto" }) })
    };

    private static async Task<(RemoteServer Server, RemoteHost Host, Store Store)> Host(Func<JsonObject, JsonNode?> respond)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-mobile-start", Guid.NewGuid().ToString("N")); var port = Port();
        var server = new RemoteServer(Path.Combine(directory, "host"), "127.0.0.1", port, request => Task.FromResult(respond(request)));
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(Path.Combine(directory, "host"), "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(directory, "phone"));
        return (server, host, new Store(Path.Combine(directory, "phone")));
    }

    [AvaloniaFact]
    public async Task OpensTheMostRecentChatAndEditsOptionsInADrawer()
    {
        var now = DateTimeOffset.UtcNow; var changes = new List<string>();
        var (server, host, store) = await Host(request => request["method"]!.GetValue<string>() switch
        {
            "list" => new JsonObject
            {
                ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "one", ["name"] = "One" }, new JsonObject { ["id"] = "two", ["name"] = "Two" }),
                ["chats"] = new JsonArray(Row("older", "one", now.AddHours(-2)), Row("newest", "two", now.AddMinutes(-1)))
            },
            "chat" => Chat(),
            "config" => Record(changes, request),
            _ => new JsonObject()
        });
        await using var _ = server;
        using var view = new RemoteView(host, preferences: store) { OpenRecentChat = true };
        var window = new Window { Content = view, Width = 400, Height = 800 }; window.Show();
        try
        {
            await Wait(() => view.SelectedChatId == "newest");
            var summary = Named<Button>(view, "RemoteOptionsSummary");
            await Wait(() => summary.IsVisible);
            Assert.Equal("Large · Ask", summary.GetLogicalDescendants().OfType<TextBlock>().First().Text);
            Assert.False(Named<WrapPanel>(view, "RemoteConfigOptions").IsVisible);

            summary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(view.OptionsOpen);
            var mode = Named<ComboBox>(view, "RemoteOption_mode");
            Assert.Equal("Ask", mode.SelectedItem);
            mode.SelectedIndex = 1;
            await Wait(() => changes.Contains("mode=auto"));
            // Tapping outside the drawer closes it.
            window.UpdateLayout();
            window.MouseDown(new Point(200, 20), MouseButton.Left); window.MouseUp(new Point(200, 20), MouseButton.Left);
            Assert.False(view.OptionsOpen);
        }
        finally { window.Close(); }
    }
    private static JsonNode Record(List<string> changes, JsonObject request)
    {
        lock (changes) changes.Add(request["configId"] + "=" + request["value"]);
        return new JsonObject();
    }

    [AvaloniaFact]
    public async Task ReopensTheChatLastOpenedOnThePhoneUnlessTheHostMovedOn()
    {
        var now = DateTimeOffset.UtcNow;
        var (server, host, store) = await Host(request => request["method"]!.GetValue<string>() switch
        {
            "list" => new JsonObject
            {
                ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "one", ["name"] = "One" }),
                ["chats"] = new JsonArray(Row("mine", "one", now.AddHours(-3)), Row("host", "one", now.AddHours(-1)))
            },
            "chat" => Chat(),
            _ => new JsonObject()
        });
        await using var _ = server;
        var key = "lastRemoteChat:" + host.Address + ":" + host.Port;
        store.Setting(key, "mine|" + now.AddMinutes(-5).ToString("O"));
        using (var view = new RemoteView(host, preferences: store) { OpenRecentChat = true })
        {
            var window = new Window { Content = view, Width = 400, Height = 800 }; window.Show();
            try { await Wait(() => view.SelectedChatId == "mine"); }
            finally { window.Close(); }
        }
        store.Setting(key, "mine|" + now.AddHours(-2).ToString("O"));
        using (var view = new RemoteView(host, preferences: store) { OpenRecentChat = true })
        {
            var window = new Window { Content = view, Width = 400, Height = 800 }; window.Show();
            try { await Wait(() => view.SelectedChatId == "host"); }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task OffersToStartAChatOrOpenAWorkspaceInsteadOfAnEmptyChat()
    {
        var workspaces = new JsonArray();
        var (server, host, store) = await Host(request => request["method"]!.GetValue<string>() == "list"
            ? new JsonObject { ["workspaces"] = workspaces.DeepClone(), ["chats"] = new JsonArray() } : new JsonObject());
        await using var _ = server;
        using var view = new RemoteView(host, preferences: store) { OpenRecentChat = true };
        Control? requested = null; view.OpenWorkspaceRequested += anchor => requested = anchor;
        var window = new Window { Content = view, Width = 400, Height = 800 }; window.Show();
        try
        {
            var empty = Named<StackPanel>(view, "RemoteEmptyState");
            var open = Named<Button>(view, "RemoteOpenWorkspace"); var start = Named<Button>(view, "RemoteStartChat");
            await Wait(() => empty.IsVisible);
            Assert.True(open.IsVisible); Assert.False(start.IsVisible);
            Assert.False(Named<ChatComposer>(view, "RemoteComposerBorder").IsVisible);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(open, requested);

            workspaces.Add(new JsonObject { ["id"] = "one", ["name"] = "One" });
            await Wait(() => start.IsVisible);
            Assert.False(open.IsVisible); Assert.True(empty.IsVisible);
            Assert.Null(view.SelectedChatId);
        }
        finally { window.Close(); }
    }
}
