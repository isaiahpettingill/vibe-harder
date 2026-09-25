using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class ChatInteractionTests
{
    [AvaloniaFact]
    public async Task ProgressResetsWhenHiddenAndStopsWhenDetached()
    {
        var progress = new ChatProgressIndicator { IsVisible = true };
        var window = new Window { Content = progress }; window.Show();
        try
        {
            await Task.Delay(420); Assert.NotEqual("·", progress.Text);
            progress.IsVisible = false; Assert.Equal("·", progress.Text);
            await Task.Delay(420); Assert.Equal("·", progress.Text);
            progress.IsVisible = true; window.Content = null;
            var text = progress.Text; await Task.Delay(420); Assert.Equal(text, progress.Text);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task RemoteExportCopiesEveryPageWithoutStartingAnAgent()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("copy-whole-chat-").FullName);
        var workspace = new Workspace("w", "Export", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", RetainHistory = false }; store.Save(chat);
        for (var i = 0; i < 137; i++) store.SaveMessage(chat, new Message { Role = "user", Text = "Message " + i });
        using var service = new SessionService(store, [workspace], [chat], (_, _) => throw new InvalidOperationException("Copy must not start an agent"));
        var text = new List<string>(); var after = -1;
        while (true)
        {
            var page = await service.Handle(new JsonObject { ["method"] = "chat/export", ["chatId"] = chat.Id, ["after"] = after });
            text.AddRange(page!["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()));
            if (page["after"] is null) break;
            after = page["after"]!.GetValue<int>();
        }
        Assert.Equal(Enumerable.Range(0, 137).Select(i => "Message " + i), text);
    }
    [Fact]
    public async Task CopiedChatsKeepCommandsButOmitToolOutput()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("copy-chat-tools-").FullName);
        var workspace = new Workspace("w", "Export", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", RetainHistory = false }; store.Save(chat);
        const string tool = "Run tests\n\n*completed*\n\n```\ndotnet test\n```\n\nPassed 412 tests\nSECRET OUTPUT";
        store.SaveMessage(chat, new Message { Role = "user", Text = "check it" });
        store.SaveMessage(chat, new Message { Role = "tool", Text = tool });
        var copied = await store.ExportChatAsync(chat);
        Assert.Contains("dotnet test", copied.Plain); Assert.DoesNotContain("SECRET OUTPUT", copied.Plain); Assert.DoesNotContain("SECRET OUTPUT", copied.Html);
        using var service = new SessionService(store, [workspace], [chat], (_, _) => throw new InvalidOperationException("Copy must not start an agent"));
        var page = await service.Handle(new JsonObject { ["method"] = "chat/export", ["chatId"] = chat.Id, ["after"] = -1 });
        var texts = page!["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal(["check it", "Run tests\n\n*completed*\n\n```\ndotnet test\n```"], texts);
    }
}
