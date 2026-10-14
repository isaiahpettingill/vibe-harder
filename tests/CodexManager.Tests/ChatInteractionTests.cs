using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class ChatInteractionTests
{
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
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
    // On a phone a finger held still on a message opens its actions; one that scrolls or lifts
    // early does not. Select text puts the message in a box where part of it can be selected.
    [Trait("Category", "Integration")]
    [AvaloniaTheory]
    [InlineData(650, 0, true)]
    [InlineData(650, 40, false)]
    [InlineData(150, 0, false)]
    public async Task HoldingAMessageOpensItsActionsWithSelectableText(int heldMs, double moved, bool opens)
    {
        var directory = Directory.CreateTempSubdirectory("hold-message-").FullName;
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Hold", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        const string text = "Some **answer** worth\nkeeping a piece of";
        store.SaveMessage(chat, new Message { Role = "user", Text = "question" });
        store.SaveMessage(chat, new Message { Role = "assistant", Text = text });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var view = new MainView(store, [workspace], [chat]);
        var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
        var popups = new List<Popup>();
        using var subscription = Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) => { if (popup.IsOpen) popups.Add(popup); });
        try
        {
            MessageView? Answer() => view.GetVisualDescendants().OfType<MessageView>().FirstOrDefault(m => m.Message?.Text == text);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Answer() is null && DateTime.UtcNow < until) { window.UpdateLayout(); await Task.Delay(25); }
            await ComposerInputTests.Touch(Answer()!, window, heldMs, moved);
            await Task.Delay(300);
            var select = popups.SelectMany(p => p.Child?.GetLogicalDescendants().OfType<MenuItem>() ?? []).FirstOrDefault(i => i.Name == "SelectText");
            Assert.Equal(opens, select is not null);
            if (select is null) return;
            select.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            var box = popups.Select(p => p.Child).OfType<Control>().SelectMany(c => c.GetLogicalDescendants().OfType<TextBox>().Prepend(c as TextBox)).OfType<TextBox>().Single(b => b.Name == "SelectedMessageText");
            Assert.True(box.IsReadOnly);
            Assert.Equal(text, box.Text);
        }
        finally { foreach (var popup in popups) popup.IsOpen = false; window.Close(); }
    }
}
