namespace CodexManager.Tests;

public class HistoryWindowTests
{
    [Trait("Category", "CI")]
    [Fact]
    public void BoundsRecentAndOlderPagesByTurnsAndMessages()
    {
        var messages = Enumerable.Range(0, 25).SelectMany(turn => new[]
        {
            new Message { Sequence = turn * 2, Role = "user", Text = $"Question {turn}" },
            new Message { Sequence = turn * 2 + 1, Role = "assistant", Text = $"Answer {turn}" }
        }).ToArray();
        var recent = HistoryWindow.Bound(messages, newer: true);
        var older = HistoryWindow.Bound(messages, newer: false);
        Assert.Equal(24, recent.Length);
        Assert.Equal(26, recent[0].Sequence);
        Assert.Equal(24, older.Length);
        Assert.Equal(0, older[0].Sequence);
        Assert.Equal(23, older[^1].Sequence);
        // Actions do not count towards the window; a window of nothing but actions is capped.
        var toolHeavy = Enumerable.Range(0, 1500).Select(i => new Message { Sequence = i, Role = "tool" });
        Assert.Equal(HistoryWindow.MaxMessages, HistoryWindow.Bound(toolHeavy, newer: true).Length);
    }

    [Trait("Category", "CI")]
    [Fact]
    public async Task ActiveChatUnloadsOlderTurnsButKeepsThemOnDisk()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "vibe-turn-window", Guid.NewGuid().ToString("N")), backgroundWrites: true);
        store.Save(new Workspace("w", "Test", "."));
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        for (var turn = 0; turn < 25; turn++)
            foreach (var message in new[]
            {
                new Message { Role = "user", Text = $"Question {turn}" },
                new Message { Role = "assistant", Text = $"Answer {turn}" }
            })
            {
                chat.Messages.Add(message);
                store.TrimHistory(chat);
                store.SaveMessage(chat, message);
            }
        Assert.Equal(24, chat.Messages.Count);
        Assert.Equal("Question 13", chat.Messages[0].Text);
        var older = await store.ReadPageAsync(chat, before: chat.Messages[0].Sequence, token: TestContext.Current.CancellationToken);
        Assert.Contains(older, m => m.Text == "Question 0");
    }

    [Trait("Category", "CI")]
    [Fact]
    public void NavigationKeepsTheVisibleEdgeWhilePagingInEitherDirection()
    {
        var messages = Enumerable.Range(0, 200).Select(i => new Message { Sequence = i, Role = "user", Text = $"Turn {i}" }).ToArray();
        var initial = messages[136..];
        var older = HistoryWindow.Navigate(initial, messages[116..136], newer: false);
        Assert.Equal(64, older.Length);
        Assert.Equal(116, older[0].Sequence);
        Assert.Equal(179, older[^1].Sequence);
        Assert.Equal(44, older.Count(m => initial.Contains(m)));
        var restored = HistoryWindow.Navigate(older, messages[180..200], newer: true);
        Assert.Equal(initial.Select(m => m.Id), restored.Select(m => m.Id));
        for (var first = 96; first >= 0; first -= 20)
        {
            older = HistoryWindow.Navigate(older, messages[first..(first + 20)], newer: false);
            Assert.True(older.Length <= Chat.HistoryPageSize);
        }
    }
    // A chat as one letter per message: u = user, a = assistant, t = tool call, h = thinking.
    // Windows count conversation messages; the actions between them come along.
    private static Message[] Chat_(string roles) => roles.Where(c => c != ' ').Select((c, i) => new Message { Sequence = i, Role = c switch { 'u' => "user", 'a' => "assistant", 't' => "tool", _ => "thought" } }).ToArray();
    private static string Roles(IEnumerable<Message> messages) => new(messages.Select(m => m.Role switch { "user" => 'u', "assistant" => 'a', "tool" => 't', _ => 'h' }).ToArray());

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("u a u ttt a u hhttt a", 2, "uhhttta")]
    [InlineData("u a u ttt a u hhttt a", 3, "a uhhttta")]
    [InlineData("u tttttttt a", 1, "a")]
    [InlineData("ttt", 5, "ttt")]
    public void NewestWindowCountsConversationMessages(string chat, int rows, string kept) =>
        Assert.Equal(kept.Replace(" ", ""), Roles(HistoryWindow.Last(Chat_(chat), rows)));

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("u a u ttt a u hhttt a", 2, "ua")]
    [InlineData("u a u ttt a u hhttt a", 3, "uau ttt")]
    [InlineData("u tttttttt a", 1, "utttttttt")]
    public void OldestWindowCountsConversationMessagesAndKeepsTheirActions(string chat, int rows, string kept) =>
        Assert.Equal(kept.Replace(" ", ""), Roles(HistoryWindow.First(Chat_(chat), rows)));

    // Reading pages from the store counts the same way in both directions.
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("u a u ttt a u hhttt a", null, false, 2, "uhhttta")]
    [InlineData("u a u ttt a u hhttt a", 7, false, 2, "u ttt a")]
    [InlineData("u a u ttt a u hhttt a", 1, true, 2, "u ttt a")]
    [InlineData("u a u ttt a u hhttt a", 6, true, 5, "u hhttt a")]
    public async Task StorePagesCountConversationMessages(string chat, int? from, bool newer, int rows, string expected)
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "vibe-row-pages", Guid.NewGuid().ToString("N")));
        store.Save(new Workspace("w", "Test", "."));
        var saved = new Chat { WorkspaceId = "w" }; store.Save(saved);
        foreach (var message in Chat_(chat)) store.SaveMessage(saved, message);
        var page = await store.ReadPageAsync(saved, from, rows, TestContext.Current.CancellationToken, newer: newer, rows: true);
        Assert.Equal(expected.Replace(" ", ""), Roles(page));
    }
}
