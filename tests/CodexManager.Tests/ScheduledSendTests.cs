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
        finally
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "sessions.db") }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(path, recursive: true);
        }
    }
}
