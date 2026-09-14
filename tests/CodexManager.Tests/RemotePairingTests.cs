using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

namespace CodexManager.Tests;

public class RemotePairingTests
{
    [AvaloniaFact]
    public async Task HiddenRemoteViewUpdatesCompletionWithoutSelectingChat()
    {
        var directory = DirectoryPath(); var port = Port(); var done = false; var sawBusy = false; var sawDone = false;
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "w", ["name"] = "Workspace" }),
            ["chats"] = new JsonArray(new JsonObject { ["id"] = "c", ["workspaceId"] = "w", ["title"] = "Chat", ["provider"] = "Codex", ["busy"] = !done, ["unread"] = done, ["status"] = done ? "Ready" : "Working", ["archived"] = false }),
            ["permissions"] = new JsonArray()
        }));
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        using var view = new RemoteView(host); view.SetPresentationSleeping(true);
        view.CatalogChanged += catalog =>
        {
            sawBusy |= catalog["chats"]?[0]?["busy"]?.GetValue<bool>() == true;
            sawDone |= catalog["chats"]?[0]?["unread"]?.GetValue<bool>() == true;
        };
        await Wait(() => sawBusy);
        var selected = view.SelectedChatId;
        done = true;
        await Wait(() => sawDone);
        Assert.Equal(selected, view.SelectedChatId);
    }
    [AvaloniaFact]
    public async Task TerminalRetainsSessionAndRetriesAfterTransportLoss()
    {
        var online = true; var reads = 0;
        using var terminal = new RemoteTerminalView(request =>
        {
            var method = request["method"]!.GetValue<string>();
            if (method == "terminal/open") return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = "persistent-shell" });
            if (method == "terminal/read")
            {
                reads++;
                Assert.Equal("persistent-shell", request["terminalId"]!.GetValue<string>());
                return Task.FromResult<JsonNode?>(online ? new JsonObject { ["text"] = "", ["offset"] = 0L } : null);
            }
            return Task.FromResult<JsonNode?>(JsonValue.Create(true));
        });
        var window = new Window { Content = terminal }; window.Show();
        try
        {
            await terminal.Open("workspace", "Host shell");
            online = false; await Wait(() => !terminal.InputReady);
            Assert.Equal("persistent-shell", terminal.TerminalId);
            var before = reads; online = true; await Wait(() => reads > before && terminal.InputReady);
            Assert.Equal("persistent-shell", terminal.TerminalId);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task QueuedPollTimeoutKeepsHealthyConnectionAndActiveRequest()
    {
        var directory = DirectoryPath(); var port = Port();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RemoteServer(directory, "127.0.0.1", port, async request =>
        {
            if (request["method"]!.GetValue<string>() == "slow") { started.SetResult(); await release.Task; }
            return JsonValue.Create("ready");
        });
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        using var client = new RemoteConnection(host); await client.Connect(TestContext.Current.CancellationToken);
        var active = client.Request(new() { ["method"] = "slow" }, TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var timeout = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<RemoteRequestBusyException>(() => client.Request(new() { ["method"] = "chat" }, timeout.Token));
            Assert.False(active.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("ready", (await active)!.GetValue<string>());
        Assert.Equal("ready", (await client.Request(new() { ["method"] = "list" }, TestContext.Current.CancellationToken))!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task EmptyRemoteWorkspaceReconnectsWithoutUserInput()
    {
        var directory = DirectoryPath(); var port = Port(); var lists = 0;
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request =>
        {
            lists++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["workspaces"] = new JsonArray(), ["chats"] = new JsonArray() });
        });
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        using var view = new RemoteView(host); var window = new Window { Content = view }; window.Show();
        try
        {
            await Wait(() => lists > 0);
            var field = typeof(RemoteView).GetField("connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var original = Assert.IsType<RemoteConnection>(field.GetValue(view)); original.Dispose();
            await Wait(() => field.GetValue(view) is RemoteConnection replacement && !ReferenceEquals(replacement, original) && lists > 1);
            Assert.Null(view.SelectedChatId);
        }
        finally { window.Close(); }
    }
    [Theory]
    [InlineData("desktop", "desktop", 2222)]
    [InlineData("desktop.tail123.ts.net", "desktop.tail123.ts.net", 2222)]
    [InlineData("  desktop:4567  ", "desktop", 4567)]
    [InlineData("100.80.90.10", "100.80.90.10", 2222)]
    [InlineData("[::1]:4567", "::1", 4567)]
    [InlineData("fd7a:115c:a1e0::1234", "fd7a:115c:a1e0::1234", 2222)]
    public void AddressesAcceptDnsAndOptionalPorts(string input, string host, int port) => Assert.Equal((host, port), RemotePairingSession.ParseAddress(input));

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData("https://host/path")]
    public void InvalidEndpointsHaveActionableErrors(string address) => Assert.Throws<FormatException>(() => RemotePairingSession.ParseAddress(address));

    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private static string DirectoryPath() => Path.Combine(Path.GetTempPath(), "vibe-pairing", Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task WorkspaceComputerPickerKeepsLocalAndRemoteFoldersSeparate()
    {
        var directory = DirectoryPath(); var port = Port(); string? openedDistro = null;
        await using var server = new RemoteServer(Path.Combine(directory, "host"), "127.0.0.1", port, request =>
        {
            JsonNode response = request["method"]!.GetValue<string>() switch
            {
                "list" => new JsonObject { ["platform"] = "linux", ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "remote", ["name"] = "Remote project", ["path"] = "/srv/project" }), ["chats"] = new JsonArray() },
                "locations" => new JsonObject { ["distros"] = new JsonArray("Debian") },
                "directories" => new JsonObject { ["path"] = "/srv/project", ["parent"] = "/srv", ["directories"] = new JsonArray() },
                "workspace" => JsonValue.Create("opened-remote")!,
                _ => throw new InvalidOperationException("Unexpected routing: " + request["method"])
            };
            if (request["method"]!.GetValue<string>() == "workspace") openedDistro = request["distro"]?.GetValue<string>();
            return Task.FromResult<JsonNode?>(response);
        });
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(Path.Combine(directory, "host"), "localhost", port, "Remote Linux"), Path.Combine(directory, "key"), "Desktop", TestContext.Current.CancellationToken);
        using var store = new Store(Path.Combine(directory, "client")); var local = new Workspace("local", "Local project", directory); store.Save(local); RemoteSettings.SaveHosts(store, [host]);
        using var picker = new WorkspaceComputerPicker(store, remoteOnly: false); var window = new Window { Content = picker }; window.Show();
        string? chosen = null; picker.RemoteChosen += (computer, id) => { Assert.Equal(host, computer); chosen = id; };
        T Field<T>(string name) where T : Control => picker.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        try
        {
            var computers = Field<ComboBox>("WorkspaceComputer"); Assert.Equal(2, computers.ItemCount);
            Assert.Equal("This computer", computers.SelectedItem!.ToString());
            Assert.Equal(local, Assert.Single(Field<ListBox>("WorkspaceHistoryList").Items.OfType<Workspace>()));
            computers.SelectedIndex = 1;
            await Wait(() => picker.GetLogicalDescendants().OfType<ListBox>().Any(l => l.Name == "WorkspaceHistoryList" && l.Items.OfType<Workspace>().Any(w => w.Id == "remote")));
            Field<Button>("BrowseWorkspaceHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => picker.GetLogicalDescendants().OfType<ComboBox>().Any(c => c.Name == "RemoteLocation" && c.ItemCount == 2));
            var locations = Field<ComboBox>("RemoteLocation"); Assert.StartsWith("Files on ", locations.Items[0]!.ToString()); Assert.EndsWith("(WSL)", locations.Items[1]!.ToString());
            Assert.DoesNotContain(locations.Items, item => item!.ToString() == "Local" || item.ToString() == WorkspaceDialog.RemoteOption);
            locations.SelectedIndex = 1; await Wait(() => Field<TextBox>("RemoteFolderPath").Text == "/srv/project");
            Field<Button>("OpenRemoteFolder").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chosen is not null); Assert.Equal("opened-remote", chosen); Assert.Equal("Debian", openedDistro);
            Assert.Equal(local, Assert.Single(store.Workspaces()));
            computers.SelectedIndex = 0; Assert.Equal(local, Assert.Single(Field<ListBox>("WorkspaceHistoryList").Items.OfType<Workspace>()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledRemoteRequestUnblocksAndFreshConnectionWorks()
    {
        var directory = DirectoryPath(); var port = Port();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request =>
        {
            if (request["method"]!.GetValue<string>() == "hang") { started.TrySetResult(); return finish.Task; }
            return Task.FromResult<JsonNode?>(JsonValue.Create("ready"));
        });
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        try
        {
            using var client = new RemoteConnection(host); await client.Connect(TestContext.Current.CancellationToken);
            using var cancel = new CancellationTokenSource();
            var pending = client.Request(new() { ["method"] = "hang" }, cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(pending.IsCompleted);
            using var replacement = new RemoteConnection(host); await replacement.Connect(TestContext.Current.CancellationToken);
            Assert.Equal("ready", (await replacement.Request(new() { ["method"] = "list" }, TestContext.Current.CancellationToken))!.GetValue<string>());
        }
        finally { finish.TrySetResult(null); }
    }

    [AvaloniaFact]
    public async Task CatalogRefreshAndResumeKeepSelectedChatAndReuseConnection()
    {
        var directory = DirectoryPath(); var port = Port(); var revision = 0;
        JsonObject Row(string id) => new() { ["id"] = id, ["workspaceId"] = "w", ["title"] = id + revision, ["archived"] = false };
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request => Task.FromResult<JsonNode?>(
            request["method"]!.GetValue<string>() == "list"
                ? new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "w", ["name"] = "Workspace" }), ["chats"] = new JsonArray(Row("newest"), Row("older")) }
                : request["method"]!.GetValue<string>() == "chat"
                    ? new JsonObject { ["status"] = "Ready", ["queued"] = 0, ["busy"] = false, ["config"] = new JsonArray(), ["messages"] = new JsonArray(), ["permissions"] = new JsonArray(), ["queue"] = new JsonArray() }
                    : JsonValue.Create(true)));
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Phone", TestContext.Current.CancellationToken);
        using var view = new RemoteView(host); var window = new Window { Content = view }; window.Show();
        try
        {
            await Wait(() => view.SelectedChatId == "newest"); view.SelectChat("older");
            var field = typeof(RemoteView).GetField("connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var original = Assert.IsType<RemoteConnection>(field.GetValue(view));
            var refresh = typeof(RemoteView).GetMethod("RefreshList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            for (var i = 0; i < 3; i++)
            {
                revision++; await (Task)refresh.Invoke(view, null)!; Assert.Equal("older", view.SelectedChatId);
                view.SetPresentationSleeping(true); view.SetPresentationSleeping(false);
                Assert.Same(original, field.GetValue(view)); Assert.Equal("older", view.SelectedChatId);
            }
            original.Dispose();
            await Wait(() => field.GetValue(view) is RemoteConnection replacement && !ReferenceEquals(replacement, original));
            Assert.Equal("older", view.SelectedChatId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DesktopPopupPairsByHostnameAndSavedCredentialReconnects()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); var port = Port(); store.Setting("remotePort", port.ToString()); store.Setting("runInTray", "0");
        var window = new MainWindow(store); window.Show();
        try
        {
            await Wait(() => File.Exists(Path.Combine(directory, "remote", "host_fingerprint")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var pairing = await RemotePairingSession.Start("localhost:" + port, "Test phone", timeout.Token);
            var popup = window.OwnedWindows.Single(w => w.Title == "Pair a device");
            var code = popup.GetLogicalDescendants().OfType<TextBlock>().Single(b => b.Name == "DesktopPairingCode").Text!;
            Assert.Matches("^[0-9]{6}$", code);
            var credentialPath = Path.Combine(directory, "phone.json");
            var host = await pairing.Complete(code, credentialPath, timeout.Token);
            Assert.Equal("localhost", host.Address);
            Assert.True(File.Exists(credentialPath));
            await Wait(() => !window.OwnedWindows.Contains(popup));
            using var connected = new RemoteConnection(host); await connected.Connect(timeout.Token);
            var catalog = await connected.Request(new() { ["method"] = "list" }, timeout.Token);
            Assert.NotNull(catalog!["workspaces"]);
            using var wrongPin = new RemoteConnection(host with { Fingerprint = "wrong" });
            await Assert.ThrowsAnyAsync<Exception>(() => wrongPin.Connect(timeout.Token));
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }

    [AvaloniaFact]
    public async Task WrongNumberDoesNotSaveCredentialsAndPairingCanBeRetried()
    {
        var directory = DirectoryPath(); var port = Port(); string? code = null;
        await using var server = new RemoteServer(directory, "127.0.0.1", port, _ => Task.FromResult<JsonNode?>(JsonValue.Create(true)), (_, number, _, _) => { code = number; return Task.CompletedTask; });
        await Wait(() => server.Fingerprint is not null || server.Error is not null); Assert.Null(server.Error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = Path.Combine(directory, "credential.json");
        using (var bad = await RemotePairingSession.Start("localhost:" + port, "Phone", timeout.Token))
        {
            var wrong = code == "000000" ? "111111" : "000000";
            await Assert.ThrowsAsync<RemoteOperationException>(() => bad.Complete(wrong, path, timeout.Token));
        }
        Assert.False(File.Exists(path)); Assert.Empty(RemoteTrust.Devices(directory));
        using var retry = await RemotePairingSession.Start("localhost:" + port, "Phone", timeout.Token);
        var host = await retry.Complete(code!, path, timeout.Token);
        using var connected = new RemoteConnection(host); await connected.Connect(timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => retry.Complete(code!, path, timeout.Token));
    }

    [AvaloniaFact]
    public async Task SavedHostOpensSharedRemoteChatInMobileShell()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); var port = Port();
        var hostDirectory = Path.Combine(directory, "host");
        await using var server = new RemoteServer(hostDirectory, "127.0.0.1", port, request => Task.FromResult<JsonNode?>(
            request["method"]!.GetValue<string>() == "list"
                ? new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "w", ["name"] = "Remote project" }), ["chats"] = new JsonArray() }
                : JsonValue.Create(true)));
        await Wait(() => server.Fingerprint is not null || server.Error is not null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(hostDirectory, "localhost", port, "Test host"), Path.Combine(directory, "key"), "Phone", timeout.Token);
        RemoteSettings.SaveHosts(store, [host]);
        var view = new MainView(store, remoteOnly: true);
        var window = new Window { Width = 390, Height = 780, Content = view };
        try
        {
            window.Show();
            await Wait(() => view.GetLogicalDescendants().OfType<Button>().Any(c => Equals(c.Content, "Remote project · Files on Test host")));
            Assert.True(view.FindControl<TextBox>("SearchBox")!.IsVisible);
            Assert.True(view.FindControl<Button>("ImportChatsButton")!.IsVisible);
            Assert.True(view.FindControl<IconButton>("MobileTerminalButton")!.IsVisible);
            Assert.Single(view.GetLogicalDescendants().OfType<RemoteView>());
            Assert.False(view.FindControl<IconButton>("ToggleTerminalButton")!.IsVisible);
            // Closing mobile settings rebuilds the host sections. A status
            // control still owned by the old section must not be reparented.
            for (var i = 0; i < 2; i++)
            {
                view.FindControl<IconButton>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(view.GetLogicalDescendants().OfType<ConnectionSettingsView>());
                view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Done")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(view.GetLogicalDescendants().OfType<ConnectionSettingsView>());
                Assert.Contains(view.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Remote project · Files on Test host"));
            }
        }
        finally { view.DisposeMobile(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingToLocalKeepsRemoteSidebarAndDraft()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("local", "Local project", directory)); store.Save(new Chat { Id = "local-chat", WorkspaceId = "local" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var port = Port(); var hostDirectory = Path.Combine(directory, "host");
        await using var server = new RemoteServer(hostDirectory, "127.0.0.1", port, request => Task.FromResult<JsonNode?>(
            request["method"]!.GetValue<string>() == "list"
                ? new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "remote", ["name"] = "Remote project" }), ["chats"] = new JsonArray(new JsonObject { ["id"] = "remote-chat", ["workspaceId"] = "remote", ["title"] = "Remote chat", ["provider"] = "Codex", ["status"] = "Ready", ["busy"] = false, ["archived"] = false }) }
                : request["method"]!.GetValue<string>() == "chat"
                    ? new JsonObject { ["status"] = "Ready", ["queued"] = 0, ["busy"] = false, ["config"] = new JsonArray(), ["messages"] = new JsonArray(), ["permissions"] = new JsonArray(), ["queue"] = new JsonArray() }
                    : JsonValue.Create(true)));
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(hostDirectory, "localhost", port, "Test host"), Path.Combine(directory, "key"), "Desktop", TestContext.Current.CancellationToken);
        RemoteSettings.SaveHosts(store, [host]);
        var window = new MainWindow(store); window.Show();
        try
        {
            window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Remote · Test host (localhost)")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => window.GetLogicalDescendants().OfType<ListBox>().Any(l => l.Name == "Chats_remote_remote"));
            var remote = window.GetLogicalDescendants().OfType<RemoteView>().Single();
            remote.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "RemoteComposer").Text = "Remote draft";
            for (var i = 0; i < 3; i++)
            {
                var local = UiTests.Named<ListBox>(window, "Chats_local"); local.SelectedItem = local.Items[0];
                Assert.False(remote.IsVisible);
                var remoteList = UiTests.Named<ListBox>(window, "Chats_remote_remote"); Assert.Single(remoteList.Items);
                remoteList.SelectedItem = remoteList.Items[0];
                Assert.True(remote.IsVisible); Assert.Equal("remote-chat", remote.SelectedChatId);
                Assert.Equal("Remote draft", remote.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "RemoteComposer").Text);
                Assert.Same(remote, window.GetLogicalDescendants().OfType<RemoteView>().Single());
            }
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }

    [AvaloniaFact]
    public async Task ConnectionSettingsSavesPairingAndOffersSavedComputer()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using var store = new Store(directory); var port = Port(); string? number = null;
        await using var server = new RemoteServer(Path.Combine(directory, "host"), "127.0.0.1", port,
            _ => Task.FromResult<JsonNode?>(JsonValue.Create(true)), (_, code, _, _) => { number = code; return Task.CompletedTask; });
        await Wait(() => server.Fingerprint is not null || server.Error is not null); Assert.Null(server.Error);
        using var settings = new ConnectionSettingsView(store, true, () => Task.CompletedTask);
        RemoteHost? paired = null; settings.Paired += host => paired = host;
        var window = new Window { Width = 390, Height = 780, Content = settings }; window.Show();
        T Field<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        try
        {
            Field<TextBox>("ConnectionAddress").Text = "localhost:" + port;
            Field<Button>("RequestPairingCode").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => number is not null && Field<Button>("RequestPairingCode").IsEnabled);
            Assert.False(Field<TextBox>("ConnectionAddress").IsEnabled);
            Field<TextBox>("PairingNumber").Text = number;
            Field<Button>("CompletePairing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => paired is not null);
            Assert.Equal(paired, RemoteSettings.Hosts(store).Single());
            Assert.True(File.Exists(paired!.KeyPath));
            settings.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Forget")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(RemoteSettings.Hosts(store));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WorkspacePickerOffersRemotePairingAndCanReturnToLocal()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using var store = new Store(directory); var port = Port(); string? number = null;
        await using var server = new RemoteServer(Path.Combine(directory, "host"), "127.0.0.1", port,
            _ => Task.FromResult<JsonNode?>(JsonValue.Create(true)), (_, code, _, _) => { number = code; return Task.CompletedTask; });
        await Wait(() => server.Fingerprint is not null || server.Error is not null); Assert.Null(server.Error);
        var owner = new Window(); owner.Show();
        var dialog = new WorkspaceDialog(new Workspace("w", "Local", directory), store);
        var result = dialog.ShowDialog<Workspace?>(owner);
        T Field<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        try
        {
            var picker = Field<ComboBox>("HostPicker");
            await Wait(() => picker.IsEnabled && picker.Items.Contains(WorkspaceDialog.RemoteOption));
            picker.SelectedItem = WorkspaceDialog.RemoteOption;
            Assert.False(Field<TextBox>("FolderPath").IsVisible);
            Assert.True(Field<TextBox>("ConnectionAddress").IsEffectivelyVisible);
            picker.SelectedItem = WorkspaceDialog.LocalOption;
            await Wait(() => Field<TextBox>("FolderPath").Text == directory);
            Assert.True(Field<TextBox>("FolderPath").IsVisible);
            Assert.Empty(dialog.GetLogicalDescendants().OfType<ConnectionSettingsView>());
            picker.SelectedItem = WorkspaceDialog.RemoteOption;
            Field<TextBox>("ConnectionAddress").Text = "localhost:" + port;
            Field<Button>("RequestPairingCode").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => number is not null && Field<Button>("RequestPairingCode").IsEnabled);
            Field<TextBox>("PairingNumber").Text = number;
            Field<Button>("CompletePairing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => dialog.PairedHost is not null);
            Assert.Null(await result);
            Assert.Equal(dialog.PairedHost, RemoteSettings.Hosts(store).Single());
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SharedSidebarCollapsesAndMobileHidesTerminal()
    {
        var directory = DirectoryPath(); Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0");
        var view = new MainView(store, remoteOnly: true);
        var window = new Window { Width = 390, Height = 780, Content = view }; window.Show();
        try
        {
            await Task.Delay(100);
            view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Done")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sidebar = view.FindControl<Border>("Sidebar")!;
            var toggle = view.FindControl<Button>("SidebarToggle")!;
            Assert.False(sidebar.IsVisible);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.True(sidebar.IsVisible); Assert.InRange(sidebar.Bounds.Width, 200, 342);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.False(sidebar.IsVisible);
            Assert.False(view.FindControl<IconButton>("ToggleTerminalButton")!.IsVisible);
            Assert.False(view.FindControl<Grid>("TerminalDrawer")!.IsVisible);
            var artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
            using var screenshot = new RenderTargetBitmap(new PixelSize(390, 780)); screenshot.Render(window); screenshot.Save(Path.Combine(artifacts, "shared-mobile-ui.png"), PngBitmapEncoderOptions.Default);
            window.Width = 1000; window.UpdateLayout(); await Task.Delay(50);
            Assert.True(sidebar.IsVisible);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); Assert.False(sidebar.IsVisible);
        }
        finally { view.DisposeMobile(); window.Close(); }
    }
}
