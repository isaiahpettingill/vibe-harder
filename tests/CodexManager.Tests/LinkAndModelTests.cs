using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;

namespace CodexManager.Tests;

public class LinkAndModelTests
{
    [Theory]
    [InlineData("/mnt/c/Users/me/Downloads/full-report.html", @"C:\Users\me\Downloads\full-report.html")]
    [InlineData("file:///mnt/c/Users/me/report%20name.html#L12", @"C:\Users\me\report name.html")]
    [InlineData("/mnt/d/reports/report.html:42", @"D:\reports\report.html")]
    [InlineData("/mnt/c", @"C:\")]
    [InlineData("/home/me/report.html", @"\\wsl.localhost\Debian\home\me\report.html")]
    [InlineData("reports/report.html", @"\\wsl.localhost\Debian\home\me\project\reports\report.html")]
    public void WslLinksUseNativeDrivePathsForWindowsMounts(string target, string expected) =>
        Assert.Equal(expected, FileLinks.Resolve(target, new("w", "WSL", "/home/me/project", "Debian")));

    [Fact]
    public void RelativeLinksInsideWindowsMountedWorkspacesUseTheDrive() =>
        Assert.Equal(@"K:\repos\project\report.html", FileLinks.Resolve("report.html", new("w", "WSL", "/mnt/k/repos/project", "Debian")));

    [AvaloniaFact]
    public void ModelPickerRefreshesRecentsWhenOpenedWithoutTyping()
    {
        string[] recent = ["old"];
        var option = new SessionConfig("model", "Model", "select", "old", [new("old", "Old"), new("new", "New")]);
        var picker = ModelPicker.Create(option, recent, _ => Task.CompletedTask, () => recent);
        var anchor = new Button { Content = "Models", Flyout = picker };
        var window = new Window { Content = anchor }; window.Show(); window.UpdateLayout();
        try
        {
            recent = ["new", "old"]; picker.ShowAt(anchor);
            var list = Assert.IsType<StackPanel>(picker.Content).Children.OfType<ListBox>().Single();
            Assert.Equal("new", Assert.IsType<SessionValue>(list.Items[0]).Value);
        }
        finally { picker.Hide(); window.Close(); }
    }
    [AvaloniaFact]
    public void MobileModelPickerDoesNotFocusSearchAndCanSelectAfterKeyboardResize()
    {
        var option = new SessionConfig("model", "Model", "select", "old", [new("old", "Old"), new("new", "New")]);
        string? selected = null;
        var picker = ModelPicker.Create(option, ["new"], value => { selected = value; return Task.CompletedTask; }, focusSearch: () => false);
        var anchor = new Button { Content = "Models", Flyout = picker, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        var window = new Window { Content = anchor, Width = 400, Height = 800 }; window.Show(); window.UpdateLayout();
        try
        {
            picker.ShowAt(anchor); window.UpdateLayout();
            var panel = Assert.IsType<StackPanel>(picker.Content);
            var search = panel.Children.OfType<TextBox>().Single();
            var list = panel.Children.OfType<ListBox>().Single();
            Assert.False(search.IsFocused);
            Assert.True(list.IsFocused);
            search.Focus(); window.Height = 400; window.UpdateLayout();
            Assert.True(picker.IsOpen);
            search.Text = "New"; search.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
            list.SelectedIndex = 0;
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("new", selected);
        }
        finally { picker.Hide(); window.Close(); }
    }
    [Fact]
    public void RecentModelsIgnoreMalformedSettingsAndRepairOnSelection()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("model-corrupt-").FullName);
        foreach (var invalid in new[] { "not-json", "{}", "null", "[123,null,{}]" })
        {
            store.Setting("recentModels:OpenCode", invalid); Assert.Empty(ModelPicker.Recent(store, AgentProvider.OpenCode));
        }
        store.Setting("recentModels:OpenCode", "[123,\"large\",\"large\",null,\"\"]");
        Assert.Equal(new[] { "large" }, ModelPicker.Recent(store, AgentProvider.OpenCode));
        ModelPicker.Remember(store, AgentProvider.OpenCode, "small");
        Assert.Equal(new[] { "small", "large" }, ModelPicker.Recent(store, AgentProvider.OpenCode));
    }
    [Fact]
    public async Task RemoteFileDownloadPreservesNameAndBytesAcrossChunks()
    {
        var directory = Directory.CreateTempSubdirectory("link-test-").FullName;
        var path = Path.Combine(directory, "test file.bin");
        var bytes = new byte[600000]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var workspace = new Workspace("w", "Test", directory);
        var requestCount = 0;
        var progress = new List<(long Received, long Total)>();
        var downloaded = await FileLinks.Download(async request => { requestCount++; return await FileLinks.Read(request, workspace); }, "chat", "test%20file.bin:42", TestContext.Current.CancellationToken,
            (received, total) => progress.Add((received, total)));
        Assert.Equal("test file.bin", Path.GetFileName(downloaded));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded, TestContext.Current.CancellationToken)); Assert.Equal(3, requestCount);
        Assert.Equal([(262144L, 600000L), (524288L, 600000L), (600000L, 600000L)], progress);
        Assert.Equal(path, FileLinks.Resolve(new Uri(path).AbsoluteUri, workspace));
        File.Delete(downloaded); Directory.Delete(Path.GetDirectoryName(downloaded)!); File.Delete(path); Directory.Delete(directory);
    }

    [Fact]
    public async Task DownloadRejectsChangedFiles()
    {
        var directory = Directory.CreateTempSubdirectory("link-test-").FullName;
        var path = Path.Combine(directory, "file.txt"); await File.WriteAllTextAsync(path, "text", TestContext.Current.CancellationToken);
        var workspace = new Workspace("w", "Test", directory);
        await Assert.ThrowsAsync<IOException>(() => FileLinks.Read(new() { ["path"] = path, ["stamp"] = -1L }, workspace));
        File.Delete(path); Directory.Delete(directory);
    }

    [AvaloniaFact]
    public async Task ClaudeDefaultsToBypassAndKeepsItAfterReconnect()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("claude-mode-").FullName);
        var workspace = new Workspace("w", "Test", Path.GetTempPath()); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Claude }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config --claude-access");
        await runtime.Connect(); Assert.Equal("bypassPermissions", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.Reconnect(); Assert.Equal("bypassPermissions", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
    }

    [AvaloniaFact]
    public async Task OpenCodeUsesLastModelForNewChatsAndPickerFiltersRecentFirst()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("model-test-").FullName);
        var workspace = new Workspace("w", "Test", Path.GetTempPath()); store.Save(workspace);
        ModelPicker.Remember(store, AgentProvider.OpenCode, "small"); ModelPicker.Remember(store, AgentProvider.OpenCode, "large");
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.OpenCode }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config");
        await runtime.Connect(); var model = chat.ConfigOptions.Single(c => c.Id == "model"); Assert.Equal("large", model.Current);
        var picker = ModelPicker.Create(model, ModelPicker.Recent(store, AgentProvider.OpenCode), _ => Task.CompletedTask);
        var panel = Assert.IsType<StackPanel>(picker.Content); var search = panel.Children.OfType<TextBox>().Single(); var list = panel.Children.OfType<ListBox>().Single();
        Assert.Equal("large", Assert.IsType<SessionValue>(list.Items[0]).Value);
        search.Text = "SMALL"; search.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
        Assert.Equal("small", Assert.IsType<SessionValue>(Assert.Single(list.Items)).Value);
        // The migrated value is now chat-local, even if another chat changes recents.
        ModelPicker.Remember(store, AgentProvider.OpenCode, "small");
        await runtime.Reconnect();
        Assert.Equal("large", chat.ConfigOptions.Single(c => c.Id == "model").Current);
    }

    [AvaloniaFact]
    public void ModelPickerShowsFiveRecentsAndVirtualizesSearchResults()
    {
        var values = Enumerable.Range(0, 1000).Select(i => new SessionValue("model-" + i, "Model " + i)).ToArray();
        var option = new SessionConfig("model", "Model", "select", "model-0", values);
        string? selected = null;
        var picker = ModelPicker.Create(option, ["missing", "model-9", "model-8", "model-7", "model-6", "model-5", "model-4"], value => { selected = value; return Task.CompletedTask; });
        var panel = Assert.IsType<StackPanel>(picker.Content);
        var search = panel.Children.OfType<TextBox>().Single(); var list = panel.Children.OfType<ListBox>().Single();
        Assert.Equal(new[] { "model-9", "model-8", "model-7", "model-6", "model-5" }, list.Items.Cast<SessionValue>().Select(v => v.Value));
        picker.Content = null;
        var window = new Window { Content = panel, Width = 360, Height = 500 }; window.Show();
        try
        {
            search.Text = "Model"; search.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent)); window.UpdateLayout();
            Assert.Equal(1000, list.ItemCount);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 50);
            search.Text = "Model 999"; search.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
            Assert.Equal("model-999", Assert.IsType<SessionValue>(Assert.Single(list.Items)).Value);
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); Assert.Equal("model-999", selected);
            search.Text = " "; search.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent)); Assert.Equal(5, list.ItemCount);
            var fresh = ModelPicker.Create(option, [], _ => Task.CompletedTask);
            Assert.Equal("model-0", Assert.IsType<SessionValue>(Assert.Single(Assert.IsType<StackPanel>(fresh.Content).Children.OfType<ListBox>().Single().Items)).Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MarkdownProvidesCopyLinkAction()
    {
        var view = new ChatMarkdown { Text = "[Report](/C:/Reports/report.pdf:12)" };
        var window = new Window { Content = view }; window.Show();
        try
        {
            await Task.Delay(150, TestContext.Current.CancellationToken); window.UpdateLayout();
            var block = view.GetVisualDescendants().OfType<CTextBlock>().Single();
            block.RaiseEvent(new ContextRequestedEventArgs());
            var copy = Assert.IsType<MenuItem>(Assert.Single(block.ContextMenu!.Items)); Assert.Equal("Copy link", copy.Header);
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            using var data = await window.Clipboard!.TryGetDataAsync();
            Assert.Equal("/C:/Reports/report.pdf:12", await data!.TryGetTextAsync());
        }
        finally { window.Close(); }
    }
}
