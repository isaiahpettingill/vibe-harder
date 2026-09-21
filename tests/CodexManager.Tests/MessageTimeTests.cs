using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class MessageTimeTests
{
    [Fact]
    public void FormatsRecentYesterdayAndOlderTimes()
    {
        var now = new DateTimeOffset(new DateTime(2026, 9, 21, 15, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 21)));
        Assert.Equal("Just now", MessageTime.Format(now.AddSeconds(-20), now));
        Assert.Equal("1 min ago", MessageTime.Format(now.AddMinutes(-1), now));
        Assert.Equal("1 hour ago", MessageTime.Format(now.AddHours(-1), now));
        Assert.Equal("12:00", MessageTime.Format(now.AddHours(-3), now));
        Assert.Equal("Yesterday 12:30", MessageTime.Format(now.AddDays(-1).AddMinutes(-150), now));
        var old = new DateTimeOffset(new DateTime(2026, 3, 1, 13, 20, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 1)));
        Assert.Equal("1-3-26 @ 13:20", MessageTime.Format(old, now));
    }

    [AvaloniaFact]
    public async Task TimestampsSurviveStorageAndAppearOnToolHeaders()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("message-time-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var time = DateTimeOffset.UtcNow.AddMinutes(-5);
        var message = new Message { Role = "tool", Text = "Run tests", Timestamp = time };
        store.SaveMessage(chat, message);
        store.LoadMessages(chat);
        Assert.Equal(time, Assert.Single(chat.Messages).Timestamp);
        var page = await store.ReadPageAsync(chat, null, 10, TestContext.Current.CancellationToken);
        Assert.Equal(time, Assert.Single(page).Timestamp);
        var view = new MessageView { Message = message };
        var window = new Window { Content = view }; window.Show();
        try
        {
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MessageTimestamp");
            Assert.Equal("5 min ago", label.Text);
            view.Message = new Message { Timestamp = null };
            Assert.False(label.IsVisible);
        }
        finally { window.Close(); }
    }
}
