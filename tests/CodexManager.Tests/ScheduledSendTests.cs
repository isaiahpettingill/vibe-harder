using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

[Trait("Category", "CI")]
public class ScheduledSendTests
{
    [Theory]
    [InlineData(0, 0, 0, null, "2026-10-04T09:00Z", "2026-10-05T09:00Z")]
    [InlineData(0, 0, 0, null, "2026-10-05T09:00Z", null)]
    [InlineData(1, 10, 0, null, "2026-10-05T09:00Z", "2026-10-07T09:00Z")]
    [InlineData(1, 10, 0, "2026-10-06", "2026-10-05T09:00Z", null)]
    [InlineData(2, 10, 4, "2026-10-07", "2026-10-05T10:00Z", "2026-10-05T13:00Z")]
    [InlineData(2, 10, 4, "2026-10-07", "2026-10-05T21:00Z", "2026-10-07T01:00Z")]
    [InlineData(2, 10, 4, null, "2026-10-05T10:00Z", null)]
    [InlineData(1, 0, 0, null, "2026-10-05T10:00Z", null)]
    public void NextOccurrenceRespectsSchedule(int mode, int days, int hours, string? end, string after, string? expected)
    {
        var schedule = new SendSchedule(new DateTime(2026, 10, 5, 9, 0, 0), (SendRepeat)mode, days, hours, end is null ? null : DateOnly.Parse(end), "UTC");
        Assert.Equal(expected is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(expected), schedule.Next(DateTimeOffset.Parse(after)));
    }

    [Theory]
    [InlineData(1, 62, 2, true)]
    [InlineData(2, 10, 6, false)]
    public void DraftSurvivesReloadWithoutBecomingAScheduledSend(int repeat, int days, int hours, bool limitDaily)
    {
        var path = Directory.CreateTempSubdirectory("scheduled-send-draft-").FullName;
        try
        {
            var draft = new ScheduledSendDraft(new DateTime(2026, 10, 5), TimeSpan.FromHours(14), (SendRepeat)repeat, days, hours, limitDaily, null);
            using (var store = new Store(path))
            using (var scheduler = new ScheduledSends(store, [], [], (_, _) => throw new InvalidOperationException()))
                scheduler.SaveDraft("chat-a", draft);

            using var reopened = new Store(path);
            using var reloaded = new ScheduledSends(reopened, [], [], (_, _) => throw new InvalidOperationException());
            Assert.Equal(draft, reloaded.DraftFor("chat-a"));
            Assert.Null(reloaded.DraftFor("chat-b"));
            Assert.Empty(reloaded.ForChat("chat-a"));
        }
        finally { RemoveTestDirectory(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void EditPreservesAttachmentsAndUpdatesTheExistingSend(int changeByHours)
    {
        var path = Directory.CreateTempSubdirectory("scheduled-send-edit-").FullName;
        try
        {
            var original = new SendSchedule(DateTime.UtcNow.Date.AddDays(2), SendRepeat.Once, 0, 0, null, "UTC");
            var updated = original with { Start = original.Start.AddHours(changeByHours) };
            var attachment = new Attachment("agenda.txt", "text/plain", "agenda");
            string id;
            using (var store = new Store(path))
            using (var scheduler = new ScheduledSends(store, [], [], (_, _) => throw new InvalidOperationException()))
            {
                Assert.True(scheduler.Add("chat", new("Original", [attachment]), original));
                id = Assert.Single(scheduler.ForChat("chat")).Id;
                Assert.True(scheduler.Edit(id, "Revised", updated));
                Assert.False(scheduler.Edit(id, "Not saved", updated with { Start = updated.Start.AddDays(-7) }));
            }

            using var reopened = new Store(path);
            using var reloaded = new ScheduledSends(reopened, [], [], (_, _) => throw new InvalidOperationException());
            var saved = Assert.Single(reloaded.ForChat("chat"));
            Assert.Equal(id, saved.Id);
            Assert.Equal(new DateTimeOffset(updated.Start), saved.Due);
            Assert.Equal("Revised", saved.Input.Text);
            Assert.Equal(attachment, Assert.Single(saved.Input.Attachments));
            reloaded.Remove(id);
            Assert.Throws<IOException>(() => reloaded.Edit(id, "Stale edit", updated));
            Assert.Empty(reloaded.ForChat("chat"));
        }
        finally { RemoveTestDirectory(path); }
    }

    [Theory]
    [InlineData("chat-a", "chat-b", "Prepare the weekly report")]
    [InlineData("chat-b", "chat-a", "Review the next release")]
    public void ReloadKeepsInputBoundToItsChatAndCancellationPersists(string target, string other, string text)
    {
        var path = Directory.CreateTempSubdirectory("scheduled-send-").FullName;
        try
        {
            var schedule = new SendSchedule(DateTime.UtcNow.Date.AddDays(2), SendRepeat.Once, 0, 0, null, "UTC");
            using (var store = new Store(path))
            using (var scheduler = new ScheduledSends(store, [], [], (_, _) => throw new InvalidOperationException()))
                Assert.True(scheduler.Add(target, new(text, []), schedule));

            using (var store = new Store(path))
            using (var scheduler = new ScheduledSends(store, [], [], (_, _) => throw new InvalidOperationException()))
            {
                Assert.Empty(scheduler.ForChat(other));
                var send = Assert.Single(scheduler.ForChat(target));
                Assert.Equal(text, send.Input.Text);
                scheduler.Remove(send.Id);
            }

            using var reopened = new Store(path);
            using var reloaded = new ScheduledSends(reopened, [], [], (_, _) => throw new InvalidOperationException());
            Assert.Empty(reloaded.ForChat(target));
        }
        finally { RemoveTestDirectory(path); }
    }

    private static void RemoveTestDirectory(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "sessions.db") }.ToString());
        SqliteConnection.ClearPool(connection);
        Directory.Delete(path, recursive: true);
    }
}
