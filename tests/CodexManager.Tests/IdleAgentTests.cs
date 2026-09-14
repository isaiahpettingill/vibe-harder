using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class IdleAgentTests
{
    private static string Fixture => "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"";
    [AvaloniaFact]
    public async Task StartupRestoresCachedChatWithoutStartingAnyProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-startup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "started.txt");
        var store = new Store(directory);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), Fixture + " --startup-log=\"" + marker + "\"");
        store.Save(new Workspace("w", "Test", directory));
        store.Save(new Chat { Id = "one", WorkspaceId = "w", SessionId = "fixture-session", Title = "Cached first" });
        store.Save(new Chat { Id = "two", WorkspaceId = "w", SessionId = "fixture-session", Title = "Cached second" });
        store.Setting("workspace", "w"); store.Setting("chat:w", "one");
        var window = new MainWindow(store); window.Show();
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker));
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Chats_w");
            Assert.Equal("Cached first", Assert.IsType<Chat>(list.SelectedItem).Title);
            list.SelectedItem = list.Items.OfType<Chat>().Single(c => c.Id == "two");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
        }
        finally { window.RequestExit(); await Task.Delay(200, TestContext.Current.CancellationToken); }
    }
    [AvaloniaTheory]
    [InlineData(AgentProvider.Codex)]
    [InlineData(AgentProvider.Claude)]
    [InlineData(AgentProvider.OpenCode)]
    public async Task IdleProcessStopsAfterGraceAndResumesSameChat(AgentProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-agent", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = provider }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Fixture);
        await runtime.Send("hello", []);
        var session = chat.SessionId; var count = chat.Messages.Count;
        var now = DateTimeOffset.UtcNow;
        await runtime.ReleaseIfIdle(now);
        await runtime.ReleaseIfIdle(now.AddSeconds(89)); Assert.True(runtime.IsConnected);
        await runtime.ReleaseIfIdle(now.AddSeconds(90)); Assert.False(runtime.IsConnected);
        Assert.Equal(session, chat.SessionId); Assert.Equal(count, chat.Messages.Count);
        await runtime.Send("again", []); Assert.True(runtime.IsConnected); Assert.Equal(session, chat.SessionId);
    }
    [AvaloniaFact]
    public async Task RemoteStartupPollReadsCacheWithoutStartingAdapter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-remote", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", SessionId = "fixture-session", Title = "Cached chat", RetainHistory = false }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Fixture);
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        var result = await service.Handle(new() { ["method"] = "chat", ["chatId"] = chat.Id, ["activate"] = false });
        Assert.Equal("Cached chat", result!["title"]!.GetValue<string>()); Assert.False(runtime.IsConnected); Assert.False(chat.Busy);
        await service.Handle(new() { ["method"] = "chat", ["chatId"] = chat.Id, ["activate"] = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!runtime.IsConnected || chat.Busy) await Task.Delay(10, timeout.Token);
    }
    [AvaloniaFact]
    public async Task VisibleBusyAndRemoteChatsKeepTheirProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-agent", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Fixture);
        await runtime.Connect();
        var now = DateTimeOffset.UtcNow;
        runtime.IsActiveView = () => true;
        await runtime.ReleaseIfIdle(now); await runtime.ReleaseIfIdle(now.AddMinutes(5)); Assert.True(runtime.IsConnected);
        runtime.IsActiveView = () => false; chat.Busy = true;
        await runtime.ReleaseIfIdle(now.AddMinutes(6)); Assert.True(runtime.IsConnected);
        chat.Busy = false; runtime.KeepAlive();
        await runtime.ReleaseIfIdle(now); Assert.True(runtime.IsConnected);
        await runtime.ReleaseIfIdle(now.AddSeconds(16));
        await runtime.ReleaseIfIdle(now.AddSeconds(105)); Assert.True(runtime.IsConnected);
        await runtime.ReleaseIfIdle(now.AddSeconds(106)); Assert.False(runtime.IsConnected);
    }
    [AvaloniaFact]
    public async Task PermissionWarningClearsAfterAnswerAndCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-agent", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Fixture);
        var response = new TaskCompletionSource<JsonObject>();
        runtime.Permission = (_, token) => response.Task.WaitAsync(token);
        var indicator = new ChatActivityIndicator(chat);
        var window = new Window { Content = indicator }; window.Show();
        try
        {
            var send = runtime.Send("permission", []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!chat.NeedsPermission) await Task.Delay(10, timeout.Token);
            Assert.True(indicator.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "PermissionWarning").IsVisible);
            Assert.Equal("Needs permission", chat.Status);
            response.SetResult(RpcJson.Permission("allow")); await send.WaitAsync(timeout.Token);
            Assert.False(chat.NeedsPermission); Assert.NotEqual("Needs permission", chat.Status);
            response = new TaskCompletionSource<JsonObject>();
            send = runtime.Send("permission", []);
            while (!chat.NeedsPermission) await Task.Delay(10, timeout.Token);
            await runtime.Stop(); await send.WaitAsync(timeout.Token);
            Assert.False(chat.NeedsPermission); Assert.NotEqual("Needs permission", chat.Status);
        }
        finally { window.Close(); }
    }
    [Fact]
    public void CacheRestoresIdleStatusAndDoesNotRestorePendingPermission()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idle-agent", Guid.NewGuid().ToString("N"));
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Test", directory));
            store.Save(new Chat { Id = "idle", WorkspaceId = "w", Title = "Cached title", Status = "Ready" });
            store.Save(new Chat { Id = "busy", WorkspaceId = "w", Busy = true, NeedsPermission = true, Status = "Needs permission" });
        }
        using var reopened = new Store(directory);
        var chats = reopened.Chats();
        Assert.Equal("Cached title", chats.Single(c => c.Id == "idle").Title);
        Assert.Equal("Ready", chats.Single(c => c.Id == "idle").Status);
        Assert.All(chats, c => { Assert.False(c.Busy); Assert.False(c.NeedsPermission); });
        Assert.Equal("Interrupted", chats.Single(c => c.Id == "busy").Status);
    }
}
