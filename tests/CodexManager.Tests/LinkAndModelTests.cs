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
    [Fact]
    public async Task RemoteFileDownloadPreservesNameAndBytesAcrossChunks()
    {
        var directory = Directory.CreateTempSubdirectory("link-test-").FullName;
        var path = Path.Combine(directory, "test file.bin");
        var bytes = new byte[600000]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var workspace = new Workspace("w", "Test", directory);
        var requestCount = 0;
        var downloaded = await FileLinks.Download(async request => { requestCount++; return await FileLinks.Read(request, workspace); }, "chat", "test%20file.bin:42", TestContext.Current.CancellationToken);
        Assert.Equal("test file.bin", Path.GetFileName(downloaded));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded, TestContext.Current.CancellationToken)); Assert.Equal(3, requestCount);
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
