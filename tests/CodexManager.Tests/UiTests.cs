using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class UiTests
{
    [AvaloniaFact]
    public async Task WorkspaceHistoryFiltersRemovesAndStartsLastProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("empty", "Empty project", directory));
            store.Save(new Workspace("gone", "Deleted project", Path.Combine(directory, "gone")));
            store.Setting("closed:empty", "1"); store.Setting("closed:gone", "1");
            store.Setting("lastProvider", "Claude");
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), FixtureCommand);
        }
        var window = new MainWindow(); window.Show();
        window.FindControl<Button>("OpenWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(200);
        var flyout = Assert.IsType<Flyout>(Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(window.FindControl<Button>("OpenWorkspaceButton")!));
        var selector = Assert.IsType<WorkspaceSelector>(flyout.Content);
        var list = selector.GetVisualDescendants().OfType<ListBox>().Single();
        await WaitUntil(() => list.ItemCount == 1);
        selector.GetVisualDescendants().OfType<TextBox>().Single().Text = "Empty";
        Assert.Equal(1, list.ItemCount);
        selector.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitUntil(() => window.FindControl<Button>("SendButton")!.IsEnabled);
        Assert.Equal(AgentProvider.Claude, Assert.IsType<Chat>(Named<ListBox>(window, "Chats_empty").SelectedItem).Provider);
        window.Close(); await Task.Delay(200);
        using var saved = new Store(directory);
        Assert.Equal("1", saved.Setting("historyRemoved:gone"));
        Assert.Single(saved.Chats());
    }
    [AvaloniaFact]
    public async Task ClosingWorkspaceKeepsChatsAndReopeningRestoresHierarchy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("one", "First workspace", directory));
            store.Save(new Workspace("two", "Second workspace", Path.GetTempPath()));
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), FixtureCommand);
            store.Save(new Chat { WorkspaceId = "one", Provider = AgentProvider.Claude, Title = "Claude history" });
            store.Save(new Chat { WorkspaceId = "one", Provider = AgentProvider.OpenCode, Title = "OpenCode history" });
            store.Save(new Chat { WorkspaceId = "two", Title = "Codex history" });
            store.Setting("workspace", "one");
        }
        var window = new MainWindow(); window.Show();
        Assert.Equal(2, Named<ListBox>(window, "Chats_one").ItemCount);
        Assert.Equal(1, Named<ListBox>(window, "Chats_two").ItemCount);
        window.FindControl<TextBox>("Composer")!.Text = "Saved before closing";
        Named<Button>(window, "CloseWorkspace_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.FindControl<StackPanel>("WorkspaceTree")!.Children.Count == 1);
        using (var store = new Store(directory)) { Assert.Equal(3, store.Chats().Count); Assert.Equal("1", store.Setting("closed:one")); }
        window.Close(); await Task.Delay(200);
        window = new MainWindow(); window.Show();
        Assert.Single(window.FindControl<StackPanel>("WorkspaceTree")!.Children);
        window.FindControl<Button>("OpenWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var historyFlyout = Assert.IsType<Flyout>(Avalonia.Controls.Primitives.FlyoutBase.GetAttachedFlyout(window.FindControl<Button>("OpenWorkspaceButton")!));
        var historyPicker = Assert.IsType<WorkspaceSelector>(historyFlyout.Content);
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(historyPicker).OfType<Button>().Single(b => b.Name == "BrowseWorkspaceHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.IsType<WorkspaceDialog>(window.OwnedWindows.Single());
        var controls = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Control>().ToArray();
        controls.OfType<TextBox>().Single(c => c.Name == "FolderPath").Text = directory;
        controls.OfType<Button>().Single(c => c.Name == "OpenFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.FindControl<StackPanel>("WorkspaceTree")!.Children.Count == 2);
        Assert.Equal(2, Named<ListBox>(window, "Chats_one").ItemCount);
        Assert.Equal("Saved before closing", window.FindControl<TextBox>("Composer")!.Text);
        var active = Assert.IsType<Chat>(Named<ListBox>(window, "Chats_one").SelectedItem);
        window.FindControl<TextBox>("Composer")!.Text = "hang";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => active.Busy && active.Messages.Any(m => m.Text == "Working"));
        Named<Button>(window, "CloseWorkspace_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Close();
        await WaitUntil(() => !window.IsVisible);
        using var saved = new Store(directory);
        Assert.Equal("hang", saved.Chats().Single(c => c.Id == active.Id).Draft);
    }

    [AvaloniaFact]
    public async Task PastePreservesUrlsAndAuthenticationPromptAppearsInsideChat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        const string authUrl = "https://auth.example.com/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A1455&state=abc%2Bdef";
        await File.WriteAllTextAsync(Path.Combine(directory, "fake-login.mjs"), $"console.log('{authUrl}');setInterval(()=>{{}},1000);");
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Clipboard", directory));
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), FixtureCommand);
            store.Setting("Codex:localLoginCommand", "node fake-login.mjs");
        }
        var window = new MainWindow(); window.Show(); await NewChat(window, "w");
        var composer = window.FindControl<TextBox>("Composer")!;
        const string url = "https://example.com/a%20b?q=one&redirect=%2Fchat#section";
        composer.Text = "before REPLACE after"; composer.SelectionStart = 7; composer.SelectionEnd = 14;
        await window.Clipboard!.SetTextAsync(url);
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await WaitUntil(() => composer.Text == "before " + url + " after");
        Assert.Empty(Assert.IsType<Chat>(Named<ListBox>(window, "Chats_w").SelectedItem).Attachments);
        composer.Text = "auth";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.FindControl<Border>("LoginPanel")!.IsVisible);
        Assert.True(window.FindControl<Button>("LoginButton")!.IsVisible);
        Assert.False(window.FindControl<Grid>("TerminalDrawer")!.IsVisible);
        Assert.Contains("Sign in to Codex", window.FindControl<TextBlock>("LoginHeading")!.Text);
        window.FindControl<Button>("LoginButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.FindControl<TextBox>("LoginUrl")!.Text == authUrl);
        window.FindControl<Button>("CopyLoginLinkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        using (var clipboard = await window.Clipboard!.TryGetDataAsync()) Assert.Equal(authUrl, await clipboard!.TryGetTextAsync());
        var terminal = Assert.IsAssignableFrom<SvcSystems.UI.Terminal.TerminalControl>(window.FindControl<ContentControl>("LoginTerminalHost")!.Content);
        var pasted = "";
        terminal.Model!.UserInput += (_, args) => pasted += System.Text.Encoding.UTF8.GetString(args.Data.Span);
        await window.Clipboard.SetTextAsync("paste-check");
        terminal.Focus();
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await WaitUntil(() => pasted.Contains("paste-check", StringComparison.Ordinal));
        terminal.SelectAll();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Task.Delay(100);
        using (var clipboard = await window.Clipboard.TryGetDataAsync()) Assert.Contains("auth.example.com", await clipboard!.TryGetTextAsync());
        window.FindControl<Button>("CheckLoginButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !window.FindControl<Border>("LoginPanel")!.IsVisible);
        window.Close(); await Task.Delay(200);
    }

    internal static T Named<T>(MainWindow window, string name) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().Single(c => c.Name == name);
    internal static async Task NewChat(MainWindow window, string workspace, AgentProvider provider = AgentProvider.Codex)
    {
        var menu = Assert.IsType<MenuFlyout>(Named<Button>(window, "NewChat_" + workspace).Flyout);
        Assert.Equal(new[] { "Claude", "Codex", "OpenCode" }, menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == AgentProviders.Get(provider).Name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => window.FindControl<Button>("SendButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task FailedSendKeepsInputTypedWhileConnecting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Recovery", directory)); store.Setting("localCommand", FixtureCommand); foreach (var p in AgentProviders.All.Skip(1)) { store.Setting(AgentProviders.CommandKey(p.Provider, false), FixtureCommand); store.Setting(AgentProviders.CommandKey(p.Provider, true), "node /missing-fixture.mjs"); }
        }
        var window = new MainWindow(); window.Show();
        await NewChat(window, "w");
        var composer = window.FindControl<TextBox>("Composer")!;
        composer.Text = "disconnect";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        composer.Text = "My next unsent thought";
        await WaitUntil(() => composer.Text?.Contains("[Recovered interrupted request]") == true);
        Assert.StartsWith("My next unsent thought", composer.Text); Assert.EndsWith("disconnect", composer.Text);
        window.Close(); await Task.Delay(200);
        using var reopened = new Store(directory);
        Assert.Contains(reopened.Chats(), c => c.Draft.Contains("My next unsent thought") && c.Draft.Contains("disconnect"));
    }
    private static string FixtureCommand => "node " + (OperatingSystem.IsWindows() ? "'" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs").Replace("'", "''") + "'" : Hosts.Quote(Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs")));

    [AvaloniaFact]
    public async Task PaletteFiltersCommandsAndLoginUsesIntegratedTerminal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "fake-login.mjs"), "import {writeFileSync} from 'node:fs';writeFileSync('login-ran.txt',process.argv[2]);console.log('LOGIN_FLOW_READY');");
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Accounts", directory)); store.Setting("localCommand", FixtureCommand); foreach (var p in AgentProviders.All.Skip(1)) { store.Setting(AgentProviders.CommandKey(p.Provider, false), FixtureCommand); store.Setting(AgentProviders.CommandKey(p.Provider, true), "node /missing-fixture.mjs"); }
            store.Setting(AgentProviders.CommandKey(AgentProvider.OpenCode, false), FixtureCommand);
            store.Setting("OpenCode:localLoginCommand", "node fake-login.mjs opencode");
        }
        var window = new MainWindow(); window.Show();
        window.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
        var palette = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<CommandPalette>().Single();
        Assert.Empty(window.OwnedWindows);
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        T Named<T>(string name) where T : Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(palette).OfType<T>().Single(c => c.Name == name);
        Named<TextBox>("CommandQuery").Text = "new opencode";
        await WaitUntil(() => Named<ListBox>("CommandResults").ItemCount == 1);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        await WaitUntil(() => UiTests.Named<ListBox>(window, "Chats_w").SelectedItem is Chat);
        var chat = Assert.IsType<Chat>(UiTests.Named<ListBox>(window, "Chats_w").SelectedItem);
        Assert.Equal(AgentProvider.OpenCode, chat.Provider);
        Assert.Equal("Add provider", window.FindControl<Button>("LoginButton")!.Content);
        window.FindControl<Button>("LoginButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.FindControl<Border>("LoginPanel")!.IsVisible);
        Assert.True(window.FindControl<ContentControl>("LoginTerminalHost")!.IsVisible);
        Assert.False(window.FindControl<Grid>("TerminalDrawer")!.IsVisible);
        Assert.Equal(0, window.FindControl<TabControl>("TerminalTabs")!.ItemCount);
        await WaitUntil(() => File.Exists(Path.Combine(directory, "login-ran.txt")));
        Assert.Equal("opencode", await File.ReadAllTextAsync(Path.Combine(directory, "login-ran.txt")));
        window.Close(); await Task.Delay(200);
    }

    [AvaloniaFact]
    public async Task SingleSelectedFolderOpensChildAndManualPathWins()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-folders", Guid.NewGuid().ToString("N"));
        var child = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
        var owner = new Window(); owner.Show();
        var dialog = new WorkspaceDialog(new Workspace("w", "Test", directory));
        var completion = dialog.ShowDialog<Workspace?>(owner);
        T Named<T>(string name) where T : Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<T>().Single(c => c.Name == name);
        var folders = Named<ListBox>("FolderList");
        await WaitUntil(() => folders.Items.Contains(child));
        folders.SelectedItem = child;
        Assert.Equal(child, Named<TextBox>("FolderPath").Text);
        Assert.Equal("Open selected folder", Named<Button>("OpenFolderButton").Content);
        Named<Button>("OpenFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(child, (await completion)!.Path);
        var second = new WorkspaceDialog(new Workspace("w", "Test", directory));
        var next = second.ShowDialog<Workspace?>(owner); dialog = second;
        await WaitUntil(() => Named<ListBox>("FolderList").Items.Contains(child));
        Named<ListBox>("FolderList").SelectedItem = child;
        Named<TextBox>("FolderPath").Text = directory;
        Assert.Null(Named<ListBox>("FolderList").SelectedItem);
        Named<Button>("OpenFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(directory, (await next)!.Path); owner.Close();
    }
    private static async Task WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(30);
        Assert.True(ready());
    }
    [AvaloniaFact]
    public async Task AutomaticallyImportsCodexAndKeepsProviderOnNewChats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Import fixture", directory));
            store.Setting("localCommand", FixtureCommand + " --history");
            store.Setting(AgentProviders.CommandKey(AgentProvider.Claude, false), FixtureCommand + " --history");
            store.Setting(AgentProviders.CommandKey(AgentProvider.OpenCode, false), FixtureCommand + " --history");
        }
        var window = new MainWindow(); window.Show();
        var chats = UiTests.Named<ListBox>(window, "Chats_w");
        await WaitUntil(() => chats.Items.OfType<Chat>().Count(c => c.SessionId == "imported-session") == 3);
        var imported = Assert.Single(chats.Items.OfType<Chat>(), c => c.SessionId == "imported-session" && c.Provider == AgentProvider.Codex);
        chats.SelectedItem = imported;
        await WaitUntil(() => !imported.Busy && imported.Messages.Count == 2);
        Assert.Equal("Earlier question", imported.Messages[0].Text); Assert.Equal("Earlier answer", imported.Messages[1].Text);
        foreach (var provider in AgentProviders.All.Skip(1))
        {
            await NewChat(window, "w", provider.Provider);
            var chat = Assert.IsType<Chat>(chats.SelectedItem);
            Assert.Equal(provider.Provider, chat.Provider);
            Assert.Same(BrandAssets.Provider(provider.Provider), window.FindControl<Image>("ChatProviderIcon")!.Source);
            Assert.Contains(chats.Items.OfType<Chat>(), c => c.Provider == AgentProvider.Codex);
        }
        window.FindControl<Button>("ImportChatsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = window.OwnedWindows.Single();
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Button>().Single(b => b.Name == "ConfirmImportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => chats.Items.OfType<Chat>().Count(c => c.SessionId == "imported-session") == 3);
        // Re-importing must preserve all three distinct providers without duplicates.
        window.FindControl<Button>("ImportChatsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog = window.OwnedWindows.Single();
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Button>().Single(b => b.Name == "ConfirmImportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("OpenCode: imported 0 previous chats.") == true);
        Assert.Equal(3, chats.Items.OfType<Chat>().Count(c => c.SessionId == "imported-session"));
        window.Close(); await Task.Delay(200);
        using var reopened = new Store(directory);
        Assert.Equal(3, reopened.Chats().Count(c => c.SessionId == "imported-session"));
    }
    [AvaloniaFact]
    public async Task WorkspaceChatSwitchingDraftSearchAndMarkdownRender()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Setting("localCommand", FixtureCommand); foreach (var p in AgentProviders.All.Skip(1)) { store.Setting(AgentProviders.CommandKey(p.Provider, false), FixtureCommand); store.Setting(AgentProviders.CommandKey(p.Provider, true), "node /missing-fixture.mjs"); }
            store.Setting("wslCommand", "node /missing-fixture.mjs");
            store.Save(new Workspace("native", "codex_manager", Path.GetFullPath(".")));
            store.Save(new Workspace("wsl", "datalake", "/home/you/repos/datalake", "Debian"));
            var chat = new Chat { WorkspaceId = "native", Title = "Build a session manager" }; store.Save(chat);
            chat.Messages.Add(new Message { Role = "user", Text = "Build a **cross-platform** Codex workspace." });
            chat.Messages.Add(new Message { Text = "### Ready to work\n\nYour sessions stay with your project.\n\n- Windows and WSL workspaces\n- Paste images and reference files\n- Resume conversations\n\n```csharp\nawait session.ResumeAsync();\n```\n\n| Host | Shell |\n| --- | --- |\n| Windows | PowerShell |\n| WSL | bash |" });
            foreach (var m in chat.Messages) store.SaveMessage(chat, m);
            store.Setting("workspace", "native");
        }
        var window = new MainWindow(); window.Show();
        await Task.Delay(150);
        var composer = window.FindControl<TextBox>("Composer")!;
        composer.Text = "Keep this draft";
        await NewChat(window, "native");
        Assert.Equal("", composer.Text);
        var list = UiTests.Named<ListBox>(window, "Chats_native");
        list.SelectedIndex = 1;
        Assert.Equal("Keep this draft", composer.Text);
        Assert.Equal("Build a session manager", window.FindControl<TextBlock>("ChatHeading")!.Text);
        using var pasted = new RenderTargetBitmap(new PixelSize(64, 64));
        using (var drawing = pasted.CreateDrawingContext()) drawing.FillRectangle(Avalonia.Media.Brushes.Coral, new Rect(0, 0, 64, 64));
        await window.Clipboard!.SetBitmapAsync(pasted);
        composer.Focus(); composer.CaretIndex = composer.Text!.Length; window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await Task.Delay(100);
        Assert.Equal(1, window.FindControl<ItemsControl>("AttachmentList")!.ItemCount);
        var attached = Assert.IsType<Attachment>(window.FindControl<ItemsControl>("AttachmentList")!.Items[0]);
        Assert.Equal("image/png", attached.MimeType); Assert.NotNull(attached.Thumbnail);
        Assert.Equal("Keep this draft[Image #1]", composer.Text);
        await window.Clipboard!.SetTextAsync(" + pasted text"); composer.CaretIndex = composer.Text!.Length;
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"); await Task.Delay(100);
        Assert.Equal("Keep this draft[Image #1] + pasted text", composer.Text);
        var referencePath = Path.Combine(directory, "reference.txt"); await File.WriteAllTextAsync(referencePath, "Dropped reference content");
        var transfer = new DataTransfer(); transfer.Add(DataTransferItem.CreateFile(await window.StorageProvider.TryGetFileFromPathAsync(referencePath) ?? throw new IOException("Fixture file unavailable")));
        var border = window.FindControl<Border>("ComposerBorder")!;
        border.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, border, new Point(10, 10), KeyModifiers.None));
        await Task.Delay(100);
        Assert.Equal(2, window.FindControl<ItemsControl>("AttachmentList")!.ItemCount);
        window.FindControl<TextBox>("SearchBox")!.Text = "cross-platform";
        var searchDeadline = DateTime.UtcNow.AddSeconds(5);
        while (list.ItemCount != 1 && DateTime.UtcNow < searchDeadline) await Task.Delay(20);
        Assert.Equal(1, list.ItemCount);
        window.FindControl<TextBox>("SearchBox")!.Text = "";
        await Task.Delay(50);
        Assert.Equal(2, list.ItemCount);
        await Task.Delay(150);
        var artifact = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")); Directory.CreateDirectory(artifact);
        using var bitmap = new RenderTargetBitmap(new PixelSize(1220, 840), new Vector(96, 96)); bitmap.Render(window); bitmap.Save(Path.Combine(artifact, "ui-chat.png"), PngBitmapEncoderOptions.Default);
        window.Width = 840; window.Height = 620; await Task.Delay(100);
        using var compact = new RenderTargetBitmap(new PixelSize(840, 620), new Vector(96, 96)); compact.Render(window); compact.Save(Path.Combine(artifact, "ui-compact.png"), PngBitmapEncoderOptions.Default);
        var send = window.FindControl<Button>("SendButton")!;
        var sendCorner = send.TranslatePoint(new Point(send.Bounds.Width, send.Bounds.Height), border)!.Value;
        Assert.InRange(sendCorner.X, 1, border.Bounds.Width); Assert.InRange(sendCorner.Y, 1, border.Bounds.Height);
        Assert.Contains("avares://VibeHarder.UI/Assets/Fonts", composer.FontFamily.ToString());
        window.Width = 1220; window.Height = 840;

        window.FindControl<Button>("ArchiveChatButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100); Assert.Equal(1, list.ItemCount);
        window.FindControl<Button>("ArchiveViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((MenuFlyout)window.FindControl<Button>("ArchiveViewButton")!.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Archived")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, list.ItemCount); Assert.True(Assert.IsType<Chat>(list.SelectedItem).Archived);
        window.FindControl<Button>("ArchiveChatButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100); Assert.Equal(0, list.ItemCount);
        window.FindControl<Button>("ArchiveViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((MenuFlyout)window.FindControl<Button>("ArchiveViewButton")!.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Chats")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(2, list.ItemCount);
        window.Close(); await Task.Delay(100);
        using var reopened = new Store(directory);
        Assert.Contains(reopened.Chats(), c => c.Draft == "Keep this draft[Image #1] + pasted text" && c.Attachments.Count == 2);
    }
    [AvaloniaFact]
    public async Task WslFolderPickerDiscoversDistrosAndNavigates()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_WSL") != "1") return;
        await Hosts.Capture(Hosts.Info("wsl.exe", "-d", "Debian", "--exec", "mkdir", "-p", "/tmp/codex-manager-smoke"));
        var dialog = new WorkspaceDialog(new Workspace("w", "Temp", "/tmp/codex-manager-smoke", "Debian")); dialog.Show();
        T Named<T>(string name) where T : Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<T>().Single(c => c.Name == name);
        var picker = Named<ComboBox>("HostPicker"); var folderPath = Named<TextBox>("FolderPath");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (folderPath.Text != "/tmp/codex-manager-smoke" && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains("Debian", picker.Items.Cast<string>()); Assert.Equal("Debian", picker.SelectedItem);
        Assert.Equal("/tmp/codex-manager-smoke", folderPath.Text);
        Named<Button>("ParentFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (folderPath.Text != "/tmp" && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal("/tmp", folderPath.Text);
        Assert.Contains("/tmp/codex-manager-smoke", Named<ListBox>("FolderList").Items.Cast<string>());
        var artifact = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")); Directory.CreateDirectory(artifact);
        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 680), new Vector(96, 96)); bitmap.Render(dialog); bitmap.Save(Path.Combine(artifact, "ui-folders.png"), PngBitmapEncoderOptions.Default);
        dialog.Close();
    }
    [AvaloniaFact]
    public async Task TerminalTabsKeepIndependentShellsAcrossWorkspaceSwitches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Setting("localCommand", FixtureCommand); foreach (var p in AgentProviders.All.Skip(1)) { store.Setting(AgentProviders.CommandKey(p.Provider, false), FixtureCommand); store.Setting(AgentProviders.CommandKey(p.Provider, true), "node /missing-fixture.mjs"); }
            store.Setting("wslCommand", "node /missing-fixture.mjs");
            store.Save(new Workspace("one", "First", directory)); store.Save(new Workspace("two", "Second", Path.GetTempPath())); store.Setting("workspace", "one");
            store.Save(new Chat { Id = "terminal-shortcut", WorkspaceId = "one" });
        }
        var window = new MainWindow(); window.Show();
        var composer = window.FindControl<TextBox>("Composer")!;
        composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Oem3, KeyModifiers = KeyModifiers.Control });
        await Task.Delay(1000);
        window.FindControl<Button>("NewTerminalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(1000);
        var tabs = window.FindControl<TabControl>("TerminalTabs")!; Assert.Equal(2, tabs.ItemCount);
        Assert.NotSame(((TabItem)tabs.Items[0]!).Content, ((TabItem)tabs.Items[1]!).Content);
        var drawer = window.FindControl<Grid>("TerminalDrawer")!;
        Assert.True(drawer.IsVisible);
        var selectedTab = Assert.IsType<TabItem>(tabs.SelectedItem);
        var selectedTerminal = Assert.IsAssignableFrom<Control>(selectedTab.Content);
        var shortcut = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Oem3, KeyModifiers = KeyModifiers.Control };
        selectedTerminal.RaiseEvent(shortcut);
        Assert.True(shortcut.Handled);
        Assert.False(drawer.IsVisible);
        Assert.Equal(2, tabs.ItemCount);
        Assert.Same(selectedTab, tabs.SelectedItem);
        Assert.True(composer.IsFocused);
        composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Oem3, KeyModifiers = KeyModifiers.Control });
        Assert.True(drawer.IsVisible);
        Assert.Same(selectedTab, tabs.SelectedItem);
        Assert.Same(selectedTerminal, selectedTab.Content);
        var pane = window.FindControl<Grid>("ChatPane")!;
        Assert.True(drawer.TranslatePoint(default, window)!.Value.X >= pane.TranslatePoint(default, window)!.Value.X + pane.Bounds.Width);
        Assert.InRange(((TabItem)tabs.Items[0]!).Bounds.Height, 1, 32);
        var artifact = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")); Directory.CreateDirectory(artifact);
        window.UpdateLayout();
        var toggle = window.FindControl<IconButton>("ToggleTerminalButton")!;
        Assert.True(((Avalonia.Controls.Shapes.Path)toggle.Content!).Bounds.Width > 0);
        using var screenshot = new RenderTargetBitmap(new PixelSize(1220, 840)); screenshot.Render(window); screenshot.Save(Path.Combine(artifact, "ui-terminal.png"), PngBitmapEncoderOptions.Default);
        Named<Button>(window, "Workspace_two").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, tabs.ItemCount);
        Named<Button>(window, "Workspace_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, tabs.ItemCount);
        window.Close(); await Task.Delay(100);
    }
}
