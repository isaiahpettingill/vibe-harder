using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

// Desktop shows a paired computer's chats in the same chat pane as local ones.
public class RemoteDesktopTests
{
    private static string Fixture => "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config";
    private static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task Wait(Func<bool> ready, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }

    [Trait("Category", "Slow")]
    [AvaloniaFact]
    public async Task RemoteChatRunsInTheLocalChatPane()
    {
        var directory = Directory.CreateTempSubdirectory("remote-desktop-").FullName;
        // The paired computer: a real session service driving the fixture agent.
        var hostDirectory = Path.Combine(directory, "host"); Directory.CreateDirectory(hostDirectory);
        using var hostStore = new Store(hostDirectory);
        foreach (var provider in AgentProviders.All) hostStore.Setting(AgentProviders.CommandKey(provider.Provider, false), Fixture);
        var hostWorkspace = new Workspace("hw", "Host project", hostDirectory); hostStore.Save(hostWorkspace);
        var hostChat = new Chat { WorkspaceId = "hw", Title = "Remote chat" }; hostStore.Save(hostChat);
        var hostRuntimes = new Dictionary<string, ChatRuntime>(); SessionService service = null!;
        // Wired like the headless host: requests wait for a remote client's answer.
        ChatRuntime HostRuntime(Chat chat, Workspace owner)
        {
            if (hostRuntimes.TryGetValue(chat.Id, out var runtime)) return runtime;
            runtime = hostRuntimes[chat.Id] = new ChatRuntime(chat, owner, hostStore, Fixture);
            runtime.Permission = (request, token) => service.Permission(chat, request, token);
            runtime.Elicitation = (request, token) => service.Elicit(chat, request, token);
            return runtime;
        }
        using var hostService = service = new SessionService(hostStore, [hostWorkspace], [hostChat], HostRuntime);
        var port = Port();
        await using var server = new RemoteServer(hostDirectory, "127.0.0.1", port, service.Handle);
        await Wait(() => server.Fingerprint is not null);

        // This computer: one local workspace plus the paired host.
        var desktopDirectory = Path.Combine(directory, "desktop"); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", desktopDirectory);
        var store = new Store(desktopDirectory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), Fixture);
        store.Save(new Workspace("local", "Local project", desktopDirectory)); store.Save(new Chat { Id = "local-chat", WorkspaceId = "local", Title = "Local chat" });
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(hostDirectory, "localhost", port, "Test host"), Path.Combine(desktopDirectory, "key"), "Desktop", TestContext.Current.CancellationToken);
        RemoteSettings.SaveHosts(store, [host]);
        var window = new MainWindow(store); window.Show();
        try
        {
            var scope = "remote:localhost:" + port + ":";
            ListBox RemoteList() => UiTests.Named<ListBox>(window, "Chats_" + scope + "hw");
            await Wait(() => window.GetLogicalDescendants().OfType<ListBox>().Any(l => l.Name == "Chats_" + scope + "hw") && RemoteList().ItemCount == 1);
            var remoteChat = Assert.IsType<Chat>(RemoteList().Items[0]);
            Assert.Equal((scope + hostChat.Id, "Remote chat"), (remoteChat.Id, remoteChat.Title));
            RemoteList().SelectedItem = remoteChat;
            var composer = window.FindControl<ComposerEditor>("Composer")!;
            var messages = window.FindControl<ListBox>("MessageList")!;
            // The same pane: no separate remote view, and the heading names the host.
            Assert.Empty(window.GetLogicalDescendants().OfType<RemoteView>());
            Assert.Equal("Remote chat", window.FindControl<TextBlock>("ChatHeading")!.Text);
            Assert.Contains("Test host", window.FindControl<TextBlock>("WorkspaceHeading")!.Text);

            composer.Text = "hello";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => messages.Items.OfType<Message>().Any(m => m.Role == "assistant" && m.Text == "Hello **world**"));
            Assert.Contains(hostChat.Messages, m => m.Role == "user" && m.Text == "hello");
            Assert.Equal("", composer.Text);

            // Search and Copy chat read the host's transcript.
            var hits = await window.FindControl<ChatSearchBar>("ChatSearch")!.Search!("world", TestContext.Current.CancellationToken);
            Assert.Contains(hits, h => h.Preview.Contains("world"));
            window.FindControl<Button>("CopyChatButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string? copied = null;
            for (var i = 0; i < 200 && copied?.Contains("Hello **world**") != true; i++) { await Task.Delay(25); copied = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!); }
            Assert.Contains("hello", copied); Assert.Contains("Hello **world**", copied);

            // Config pickers come from the host and apply there.
            await Wait(() => window.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "Config_model"));
            var model = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "Config_model");
            var large = Assert.IsType<MenuFlyout>(model.Flyout).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Large");
            large.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Wait(() => hostChat.ConfigOptions.Single(c => c.Id == "model").Current == "large");

            // A permission request on the host is answered from this pane's card.
            composer.Text = "permission";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var permissions = window.FindControl<StackPanel>("InlinePermissions")!;
            await Wait(() => permissions.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Allow once")));
            permissions.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Allow once")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => messages.Items.OfType<Message>().Any(m => m.Role == "assistant" && m.Text.Contains("allow")));
            await Wait(() => permissions.Children.Count == 0);

            // Drafts stay with their chat when switching between local and remote.
            composer.Text = "remote draft";
            var local = UiTests.Named<ListBox>(window, "Chats_local"); local.SelectedItem = local.Items[0];
            Assert.Equal("", composer.Text);
            Assert.Equal("Local chat", window.FindControl<TextBlock>("ChatHeading")!.Text);
            RemoteList().SelectedItem = RemoteList().Items[0];
            Assert.Equal("remote draft", composer.Text);
            Assert.Null(local.SelectedItem);
            // Remote chats and workspaces never land in this computer's store.
            Assert.DoesNotContain(store.Chats(), c => c.Id.StartsWith("remote:", StringComparison.Ordinal));
            Assert.DoesNotContain(store.Workspaces(), w => w.Id.StartsWith("remote:", StringComparison.Ordinal));
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); foreach (var runtime in hostRuntimes.Values) await runtime.DisposeAsync(); }
    }

    [Trait("Category", "Slow")]
    [AvaloniaFact]
    public async Task RemoteChatsAreCreatedQueuedStoppedArchivedAndDeletedFromTheSidebar()
    {
        var directory = Directory.CreateTempSubdirectory("remote-desktop-manage-").FullName;
        var hostDirectory = Path.Combine(directory, "host"); Directory.CreateDirectory(hostDirectory);
        using var hostStore = new Store(hostDirectory);
        foreach (var provider in AgentProviders.All) hostStore.Setting(AgentProviders.CommandKey(provider.Provider, false), Fixture);
        var hostWorkspace = new Workspace("hw", "Host project", hostDirectory); hostStore.Save(hostWorkspace);
        var hostChats = new List<Chat>();
        var hostRuntimes = new Dictionary<string, ChatRuntime>();
        using var service = new SessionService(hostStore, [hostWorkspace], hostChats, (chat, owner) =>
            hostRuntimes.TryGetValue(chat.Id, out var runtime) ? runtime : hostRuntimes[chat.Id] = new ChatRuntime(chat, owner, hostStore, Fixture));
        var port = Port();
        await using var server = new RemoteServer(hostDirectory, "127.0.0.1", port, service.Handle);
        await Wait(() => server.Fingerprint is not null);
        var desktopDirectory = Path.Combine(directory, "desktop"); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", desktopDirectory);
        var store = new Store(desktopDirectory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("local", "Local project", desktopDirectory));
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(hostDirectory, "localhost", port, "Test host"), Path.Combine(desktopDirectory, "key"), "Desktop", TestContext.Current.CancellationToken);
        RemoteSettings.SaveHosts(store, [host]);
        var window = new MainWindow(store); window.Show();
        try
        {
            var scope = "remote:localhost:" + port + ":";
            await Wait(() => window.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "NewChat_" + scope + "hw"));
            // New chat from the workspace's + menu is created on the host and opened here.
            var create = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "NewChat_" + scope + "hw");
            Assert.IsType<MenuFlyout>(create.Flyout).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Codex").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Wait(() => hostChats.Count == 1);
            var hostChat = hostChats[0];
            await Wait(() => window.FindControl<TextBlock>("ChatHeading")!.Text == "New chat" && UiTests.Named<ListBox>(window, "Chats_" + scope + "hw").SelectedItem is Chat);

            // The terminal drawer opens a shell on the host for a remote workspace.
            window.FindControl<Button>("ToggleTerminalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var tabs = window.FindControl<TabControl>("TerminalTabs")!;
            await Wait(() => tabs.Items.OfType<TabItem>().SingleOrDefault()?.Content is RemoteTerminalView { TerminalId: not null });
            Assert.True(window.FindControl<Grid>("TerminalDrawer")!.IsVisible);
            window.FindControl<Button>("ToggleTerminalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.FindControl<Grid>("TerminalDrawer")!.IsVisible);
            // Remote shells outlive the desktop by design; close this one on the host.
            var terminalId = ((RemoteTerminalView)tabs.Items.OfType<TabItem>().Single().Content!).TerminalId;
            await service.Handle(new System.Text.Json.Nodes.JsonObject { ["method"] = "terminal/close", ["terminalId"] = terminalId });

            // Messages sent while the agent works are queued on the host.
            var composer = window.FindControl<ComposerEditor>("Composer")!;
            var send = window.FindControl<IconButton>("SendButton")!;
            composer.Text = "hang"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => hostChat.Busy && window.FindControl<ListBox>("MessageList")!.Items.OfType<Message>().Any(m => m.Text == "Working"));
            composer.Text = "queued follow-up"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => hostChat.QueuedInputs.Count == 1 && window.FindControl<Expander>("QueuePanel")!.IsVisible);
            window.FindControl<StackPanel>("QueueItems")!.GetVisualDescendants().OfType<IconButton>().First(b => Equals(ToolTip.GetTip(b), "Remove queued message")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => hostChat.QueuedInputs.Count == 0 && !window.FindControl<Expander>("QueuePanel")!.IsVisible);
            // Stop: the send button turns into Stop with an empty composer.
            await Wait(() => Equals(ToolTip.GetTip(send), "Stop"));
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !hostChat.Busy);

            // Archive from the sidebar row archives on the host.
            var remoteId = scope + hostChat.Id;
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "Archive_" + remoteId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => hostChat.Archived && UiTests.Named<ListBox>(window, "Chats_" + scope + "hw").ItemCount == 0);
            // Deleting it from the archive removes it on the host.
            var remoteChat = Assert.IsType<Chat>(typeof(MainView).GetField("remoteChats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window.GetLogicalDescendants().OfType<MainView>().Single()) is Dictionary<string, Chat> known ? known[remoteId] : null);
            var delete = typeof(MainView).GetMethod("DeleteChatCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<string?>)delete.Invoke(window.GetLogicalDescendants().OfType<MainView>().Single(), [remoteChat, new Workspace(scope + "hw", "Host project", hostDirectory) { Remote = host, RemoteId = "hw" }])!;
            await Wait(() => hostChats.Count == 0);
            Assert.DoesNotContain(store.Chats(), c => c.Id.StartsWith("remote:", StringComparison.Ordinal));
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); foreach (var runtime in hostRuntimes.Values) await runtime.DisposeAsync(); }
    }
}
