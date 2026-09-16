using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class ChatSearchTests
{
    [Fact]
    public async Task SearchesFullHistoryLiterallyWithoutStartingAnAgent()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("chat-search-").FullName);
        var owner = new Workspace("w", "Search", store.DirectoryPath); store.Save(owner);
        var chat = new Chat { WorkspaceId = owner.Id }; store.Save(chat);
        for (var i = 0; i < 300; i++) store.SaveMessage(chat, new Message { Text = i == 5 ? "Literal %_needle in old history" : "common text " + i });
        var other = new Chat { WorkspaceId = owner.Id }; store.Save(other);
        store.SaveMessage(other, new Message { Text = "%_needle elsewhere" });
        using var service = new SessionService(store, [owner], [chat, other], (_, _) => throw new Exception("Search must not start an agent"));
        var result = (JsonArray)(await service.Handle(new() { ["method"] = "chat/search", ["chatId"] = chat.Id, ["query"] = "%_NEEDLE" }))!;
        Assert.Single(result); Assert.Equal(5, result[0]!["sequence"]!.GetValue<int>());
        Assert.Contains("old history", result[0]!["preview"]!.GetValue<string>());
        Assert.Equal(200, (await store.SearchMessagesAsync(chat, "common", TestContext.Current.CancellationToken)).Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchMessagesAsync(chat, "common", cancelled.Token));
        Assert.Empty(chat.Messages);
    }

    [AvaloniaFact]
    public async Task SearchNavigatesAndClosingCancelsPendingResults()
    {
        var bar = new ChatSearchBar(); var navigated = new List<string>();
        var pending = new TaskCompletionSource<ChatSearchHit[]>();
        bar.Search = (query, _) => query == "wait" ? pending.Task : Task.FromResult<ChatSearchHit[]>([new("one", 1, "First"), new("two", 2, "Second")]);
        bar.Navigate = (hit, _) => { navigated.Add(hit.Id); return Task.CompletedTask; };
        var window = new Window { Content = bar, Width = 500, Height = 200 }; window.Show();
        try
        {
            bar.Open(); window.UpdateLayout();
            var query = bar.GetVisualDescendants().OfType<TextBox>().Single(); query.Text = "find";
            await Task.Delay(220, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
            Assert.Equal(["one"], navigated);
            bar.GetVisualDescendants().OfType<IconButton>().Single(b => b.Name == "NextChatSearchMatch").RaiseEvent(new(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal(["one", "two"], navigated);
            query.Text = "wait"; await Task.Delay(220, TestContext.Current.CancellationToken);
            bar.Close(); pending.SetResult([new("stale", 3, "Stale")]); await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.False(bar.IsVisible); Assert.DoesNotContain("stale", navigated);
        }
        finally { window.Close(); }
    }
}
