using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

public class AutomaticRecoveryTests
{
    [AvaloniaFact]
    public async Task ResumeSpaceIsSentAsARealTextBlock()
    {
        var directory = Directory.CreateTempSubdirectory("resume-space-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Resume", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --commands");
        await runtime.Send(" ", []);
        Assert.Contains(chat.Messages, m => m.Role == "assistant" && m.Text == " ");
    }
    [AvaloniaFact]
    public async Task NetworkErrorWithLivingAdapterKeepsRetryingUntilWifiReturns()
    {
        var directory = Directory.CreateTempSubdirectory("network-recovery-").FullName;
        using var store = new Store(directory); store.Setting("autoResume", "0");
        var workspace = new Workspace("w", "Recovery", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Codex }; store.Save(chat);
        var online = Path.Combine(directory, "online");
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" \"--network-outage=" + online + "\"");
        await runtime.Send("Finish my work", []);
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(15); while (!check() && DateTime.UtcNow < until) await Task.Delay(25); Assert.True(check()); }
        await Wait(() => runtime.IsRecovering);
        await Task.Delay(2500); Assert.True(runtime.IsRecovering); Assert.Equal("fixture-session", chat.SessionId);
        File.WriteAllText(online, "online");
        await Wait(() => !runtime.IsRecovering && chat.InterruptedInput is null);
        Assert.Contains(chat.Messages, m => m.Text == "Hello **world**");
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text == "Finish my work");
        Assert.Equal("fixture-session", chat.SessionId); Assert.Equal("0", store.Setting("autoResume"));
    }

    [Fact]
    public void AuthenticationAndInvalidRequestsAreNotNetworkFailures()
    {
        Assert.False(ChatRuntime.IsNetworkFailure(new IOException("unauthorized: connection reset")));
        Assert.False(ChatRuntime.IsNetworkFailure(new IOException("Invalid model selected")));
        Assert.True(ChatRuntime.IsNetworkFailure(new IOException("stream disconnected before completion")));
    }

    [AvaloniaFact]
    public async Task StopCancelsNetworkRecoveryBeforeConnectivityReturns()
    {
        var directory = Directory.CreateTempSubdirectory("network-stop-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Recovery", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var online = Path.Combine(directory, "online");
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" \"--network-outage=" + online + "\"");
        await runtime.Send("Finish my work", []);
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((!runtime.IsRecovering || chat.Busy) && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(runtime.IsRecovering); await runtime.Stop();
        File.WriteAllText(online, "online");
        await Task.Delay(2500);
        Assert.False(runtime.IsRecovering); Assert.Null(chat.InterruptedInput);
        Assert.DoesNotContain(chat.Messages, m => m.Text == "Hello **world**");
    }

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
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text == " ");
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
