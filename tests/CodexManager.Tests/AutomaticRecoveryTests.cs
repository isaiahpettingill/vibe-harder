using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

public class AutomaticRecoveryTests
{
    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task ResumeTellsTheAgentTheEnvironmentRestarted()
    {
        var directory = Directory.CreateTempSubdirectory("resume-space-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Resume", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var log = Path.Combine(directory, "prompts.log");
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --commands \"--prompt-log=" + log + "\"");
        await runtime.Send(" ", [], autoResume: true);
        // A blank prompt reads to the agent as an interruption, so the resume says what happened.
        Assert.Equal("[{\"type\":\"text\",\"text\":\"Environment restarted. Continue\"}]", File.ReadAllLines(log).Single());
        Assert.DoesNotContain(chat.Messages, m => m.Role == "user" && string.IsNullOrWhiteSpace(m.Text));
    }
    [Trait("Category", "Integration")]
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

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("unauthorized: connection reset", false)]
    [InlineData("Invalid model selected", false)]
    [InlineData("stream disconnected before completion", true)]
    public void OnlyTransportFailuresAreNetworkFailures(string message, bool network) => Assert.Equal(network, ChatRuntime.IsNetworkFailure(new IOException(message)));

    [Trait("Category", "Integration")]
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

    [Trait("Category", "Integration")]
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
        Assert.DoesNotContain(chat.Messages, m => m.Role == "user" && string.IsNullOrWhiteSpace(m.Text));
        Assert.Null(chat.InterruptedInput); Assert.False(chat.Busy); Assert.False(runtime.IsRecovering);
        Assert.Equal("", store.Setting("interrupted:" + chat.Id));
    }
    [Trait("Category", "Integration")]
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AutomaticResumeNeverReopensAClosedWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-closed-resume", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        string chatId;
        using (var store = new Store(directory))
        {
            store.Setting("autoResume", "1"); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
            store.Save(new Workspace("open", "Open", directory));
            store.Save(new Workspace("gone", "Removed distro", "/home/me/repo", "RemovedDistro")); store.Setting("closed:gone", "1");
            var chat = new Chat { WorkspaceId = "gone" }; store.Save(chat); chatId = chat.Id;
            store.Setting("interrupted:" + chat.Id, "{\"Text\":\"finish the migration\",\"Attachments\":[]}");
        }
        var window = new MainWindow(); window.Show();
        try
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Single(UiTests.Named<Avalonia.Controls.StackPanel>(window, "WorkspaceTree").Children);
        }
        finally { window.Close(); await Task.Delay(300, TestContext.Current.CancellationToken); }
        using var saved = new Store(directory);
        Assert.Equal("1", saved.Setting("closed:gone"));
        Assert.Equal("", saved.Setting("interrupted:" + chatId));
        // The request is kept as the chat's draft rather than lost.
        Assert.Equal("finish the migration", saved.Chats().Single(c => c.Id == chatId).Draft);
    }
    [Trait("Category", "CI")]
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
