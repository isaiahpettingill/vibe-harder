using Avalonia;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

// A paired device opens the host's own terminals, not shells of its own.
public class SharedTerminalTests
{
    private static async Task Wait(Func<bool> ready, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }

    // What the phone shows: the host's first terminal, one chosen from the switcher, a new one,
    // the next one when the host closes the shown one, or an older host's single shell.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData(new[] { "a", "b" }, "open", "a", new[] { "a", "b" })]
    [InlineData(new[] { "a", "b" }, "choose 1", "b", new[] { "a", "b" })]
    [InlineData(new[] { "a", "b" }, "new", "c", new[] { "a", "b", "c" })]
    [InlineData(new[] { "a", "b" }, "host closes", "b", new[] { "b" })]
    [InlineData(null, "open", "legacy", new[] { "legacy" })]
    public async Task ThePhoneShowsTheHostsTerminals(string[]? hostList, string action, string shown, string[] listed)
    {
        var host = hostList?.ToList(); var closed = new HashSet<string>();
        Task<JsonNode?> Call(JsonObject request)
        {
            JsonNode? result = request["method"]!.GetValue<string>() switch
            {
                "terminal/list" => host is null ? null : new JsonObject { ["terminals"] = new JsonArray(host.Select(id => (JsonNode)id).ToArray()) },
                "terminal/open" when host is null => new JsonObject { ["id"] = "legacy" },
                "terminal/open" when request["new"]?.GetValue<bool>() == true => new JsonObject { ["id"] = Added("c") },
                "terminal/open" => new JsonObject { ["id"] = request["terminalId"]?.GetValue<string>() ?? host[0] },
                "terminal/read" when closed.Contains(request["terminalId"]!.GetValue<string>()) => new JsonObject { ["error"] = "This terminal has closed. Open it again to start a new shell." },
                "terminal/read" => new JsonObject { ["text"] = "", ["offset"] = 0L },
                _ => JsonValue.Create(true),
            };
            return Task.FromResult(result);
        }
        string Added(string id) { host!.Add(id); return id; }
        var popups = new List<Popup>();
        using var subscription = Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) => { if (popup.IsOpen) popups.Add(popup); });
        using var view = new RemoteTerminalView(Call, mobile: true);
        var window = new Window { Content = view }; window.Show();
        try
        {
            await view.Open("w", "Terminal");
            async Task Pick(string name)
            {
                view.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "TerminalSwitcher").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => popups.Any(p => p.IsOpen));
                var item = popups.Last(p => p.IsOpen).Child!.GetLogicalDescendants().OfType<MenuItem>().Single(i => i.Name == name);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            if (action == "choose 1") await Pick("TerminalChoice1");
            if (action == "new") await Pick("NewHostTerminal");
            if (action == "host closes") { closed.Add("a"); host!.Remove("a"); }
            await Wait(() => view.TerminalId == shown);
            Assert.Equal(listed, view.HostTerminals);
        }
        finally { foreach (var popup in popups) popup.IsOpen = false; window.Close(); }
    }

    // The host's side of it: clients that do not choose get the host's running terminal, a new
    // terminal joins the host's tabs, and one closed remotely leaves them.
    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task RemoteClientsOpenAndCloseTheHostsOwnTerminals()
    {
        var directory = Directory.CreateTempSubdirectory("shared-terminals-").FullName;
        using var store = new Store(directory);
        using var service = new SessionService(store, [new Workspace("w", "Test", directory)], [], (_, _) => throw new InvalidOperationException());
        var announced = 0; void Count(Store changed, string workspace) { if (changed == store && workspace == "w") announced++; }
        HostTerminals.Changed += Count;
        async Task<string> Open(JsonObject extra)
        {
            var request = new JsonObject { ["method"] = "terminal/open", ["workspaceId"] = "w" };
            foreach (var (key, value) in extra) request[key] = value?.DeepClone();
            return (await service.Handle(request))!["id"]!.GetValue<string>();
        }
        async Task<string[]> List() => (await service.Handle(new JsonObject { ["method"] = "terminal/list", ["workspaceId"] = "w" }))!["terminals"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        try
        {
            var first = await Open([]);
            Assert.Equal([first], await List());
            Assert.Equal(first, await Open([]));
            var second = await Open(new JsonObject { ["new"] = true });
            Assert.Equal([first, second], HostTerminals.Ids(store, "w"));
            Assert.Equal(second, await Open(new JsonObject { ["terminalId"] = second }));
            await service.Handle(new JsonObject { ["method"] = "terminal/close", ["terminalId"] = second });
            Assert.Equal([first], await List());
            await Assert.ThrowsAsync<IOException>(() => Open(new JsonObject { ["terminalId"] = second }));
            Assert.Equal(3, announced);
            await service.Handle(new JsonObject { ["method"] = "terminal/close", ["terminalId"] = first });
            Assert.Empty(await List());
        }
        finally { HostTerminals.Changed -= Count; }
    }

    // The host's window shows a terminal a phone opens as another tab, without popping its
    // terminal pane open, and drops the tab when the phone closes it.
    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task TheHostWindowFollowsTerminalsOpenedRemotely()
    {
        var directory = Directory.CreateTempSubdirectory("shared-terminal-window-").FullName;
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory); store.Save(workspace); store.Setting("workspace", "w");
        var view = new MainView(store, [workspace], []);
        var window = new Window { Content = view, Width = 1200, Height = 800 }; view.AttachDesktop(window); window.Show();
        using var service = new SessionService(store, [workspace], [], (_, _) => throw new InvalidOperationException());
        var tabs = view.FindControl<TabControl>("TerminalTabs")!; var drawer = view.FindControl<Grid>("TerminalDrawer")!;
        try
        {
            view.FindControl<Button>("NewTerminalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => tabs.ItemCount == 1 && HostTerminals.Ids(store, "w").Length == 1);
            view.FindControl<Button>("ToggleTerminalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(drawer.IsVisible);
            var opened = (await service.Handle(new JsonObject { ["method"] = "terminal/open", ["workspaceId"] = "w", ["new"] = true }))!["id"]!.GetValue<string>();
            await Wait(() => tabs.ItemCount == 2);
            Assert.False(drawer.IsVisible);
            await service.Handle(new JsonObject { ["method"] = "terminal/close", ["terminalId"] = opened });
            await Wait(() => tabs.ItemCount == 1);
            foreach (var id in HostTerminals.Ids(store, "w")) await service.Handle(new JsonObject { ["method"] = "terminal/close", ["terminalId"] = id });
        }
        finally { view.RequestExit(); await Task.Delay(200); }
    }
}
