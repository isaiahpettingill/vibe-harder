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
            await Wait(() => view.GetLogicalDescendants().OfType<Button>().Any(c => Equals(c.Content, "Remote project")));
            Assert.True(view.FindControl<TextBox>("SearchBox")!.IsVisible);
            Assert.True(view.FindControl<Button>("ImportChatsButton")!.IsVisible);
            Assert.True(view.FindControl<IconButton>("MobileTerminalButton")!.IsVisible);
            Assert.Single(view.GetLogicalDescendants().OfType<RemoteView>());
            Assert.False(view.FindControl<IconButton>("ToggleTerminalButton")!.IsVisible);
        }
        finally { view.DisposeMobile(); window.Close(); }
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
            picker.SelectedItem = "Local";
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
