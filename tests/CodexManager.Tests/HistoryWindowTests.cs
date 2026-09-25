namespace CodexManager.Tests;

public class HistoryWindowTests
{
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
        var toolHeavy = Enumerable.Range(0, 200).Select(i => new Message { Sequence = i, Role = "tool" });
        Assert.Equal(Chat.HistoryPageSize, HistoryWindow.Bound(toolHeavy, newer: true).Length);
    }

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
}
