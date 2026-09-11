using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

public class AutomaticRecoveryTests
{
    [AvaloniaFact]
    public async Task BrokenTransportReconnectsAndContinuesWithoutRepeatingOriginalPrompt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-auto-recovery", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory); store.Setting("autoResume", "1");
        var workspace = new Workspace("w", "Recovery", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Send("disconnect", []);
        var until = DateTime.UtcNow.AddSeconds(15);
        while ((!chat.Messages.Any(m => m.Text == "Hello **world**") || runtime.IsRecovering) && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.Contains(chat.Messages, m => m.Text == "Hello **world**");
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text == "disconnect");
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text.StartsWith("Continue the interrupted request"));
        Assert.Null(chat.InterruptedInput); Assert.False(chat.Busy); Assert.False(runtime.IsRecovering);
        Assert.Equal("", store.Setting("interrupted:" + chat.Id));
    }
    [Fact]
    public void JournalRecoversMissingDatabaseMarkerAndDatabaseRecoversBrokenJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-journal", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory); store.Save(new Workspace("w", "Journal", directory));
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        const string input = "{\"Text\":\"saved objective\",\"Attachments\":[]}";
        var key = "interrupted:" + chat.Id; store.Setting(key, input);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "sessions.db")))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM settings WHERE key=$key"; command.Parameters.AddWithValue("$key", key); command.ExecuteNonQuery();
        }
        Assert.Equal("saved objective", store.Chats().Single().InterruptedInput!.Text);
        store.Setting(key, input); new RecoveryJournal(directory).Write(key, "broken");
        Assert.Equal(input, store.Setting(key));
        store.Setting(key, ""); Assert.Null(store.Chats().Single().InterruptedInput);
    }
}
