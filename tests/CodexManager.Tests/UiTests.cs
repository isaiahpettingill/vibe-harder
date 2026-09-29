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
        window.FindControl<ComposerEditor>("Composer")!.Text = "Saved before closing";
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
        Assert.Equal("Saved before closing", window.FindControl<ComposerEditor>("Composer")!.Text);
        var active = Assert.IsType<Chat>(Named<ListBox>(window, "Chats_one").SelectedItem);
        window.FindControl<ComposerEditor>("Composer")!.Text = "hang";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => active.Busy && active.Messages.Any(m => m.Text == "Working"));
        Named<Button>(window, "CloseWorkspace_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Close();
        await WaitUntil(() => !window.IsVisible);
        using var saved = new Store(directory);
        Assert.Equal("hang", saved.Chats().Single(c => c.Id == active.Id).Draft);
    }

    internal static T Named<T>(MainWindow window, string name) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<T>().Single(c => c.Name == name);
    internal static async Task NewChat(MainWindow window, string workspace, AgentProvider provider = AgentProvider.Codex)
    {
        var menu = Assert.IsType<MenuFlyout>(Named<Button>(window, "NewChat_" + workspace).Flyout);
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
        var composer = window.FindControl<ComposerEditor>("Composer")!;
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
}
