namespace CodexManager.Tests;

public class StoreTests
{
    [Fact]
    public void ExistingDatabaseMigratesWithoutChangingCodexSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-migration", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using (var original = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "sessions.db") }.ToString()))
        {
            original.Open(); using var command = original.CreateCommand();
            command.CommandText = """
                CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,path TEXT NOT NULL,distro TEXT);
                CREATE TABLE chats(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL,session_id TEXT,title TEXT NOT NULL,updated TEXT NOT NULL,draft TEXT NOT NULL DEFAULT '');
                INSERT INTO workspaces VALUES('w','Existing','/tmp',NULL);
                INSERT INTO chats VALUES('c','w','existing-session','Existing conversation','2026-09-10T12:00:00Z','Keep this draft');
                """;
            command.ExecuteNonQuery();
        }
        using var upgraded = new Store(directory);
        var chat = Assert.Single(upgraded.Chats());
        Assert.Equal(AgentProvider.Codex, chat.Provider); Assert.Equal("existing-session", chat.SessionId);
        Assert.Equal("Keep this draft", chat.Draft); Assert.False(chat.Archived);
        upgraded.Save(chat);
    }
    [Fact]
    public void RoundTripPreservesDistroSessionDraftAndOrderedTranscript()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-manager-tests", Guid.NewGuid().ToString("N"));
        var workspace = new Workspace("w", "Project", "/home/test/a b's", "Debian");
        var chat = new Chat { WorkspaceId = "w", SessionId = "acp-session", Title = "Resume me", Draft = "Unsent draft", Archived = true };
        var attachment = new Attachment("a.txt", "text/plain", "reference contents", Path.GetFullPath("a.txt"));
        chat.Attachments.Add(attachment);
        using (var db = new Store(path))
        {
            db.Save(workspace); db.Save(chat);
            foreach (var text in new[] { "first", "second" }) { var m = new Message { Text = text }; m.Attachments.Add(attachment); chat.Messages.Add(m); db.SaveMessage(chat, m); }
            chat.Messages[0].Text = "first, streamed"; db.SaveMessage(chat, chat.Messages[0]);
        }
        using var reopened = new Store(path);
        Assert.Equal(workspace, Assert.Single(reopened.Workspaces()));
        var resumed = Assert.Single(reopened.Chats()); reopened.LoadMessages(resumed);
        Assert.Equal("acp-session", resumed.SessionId); Assert.Equal("Unsent draft", resumed.Draft);
        Assert.True(resumed.Archived);
        Assert.Equal(attachment, Assert.Single(resumed.Attachments));
        Assert.Equal(new[] { "first, streamed", "second" }, resumed.Messages.Select(m => m.Text));
        Assert.Equal(attachment, Assert.Single(resumed.Messages[0].Attachments));
        reopened.Delete(resumed);
        Assert.Empty(reopened.Chats()); Assert.Single(reopened.Workspaces());
    }
    [Fact]
    public void WslArgumentsKeepPathAsOneArgument()
    {
        if (!OperatingSystem.IsWindows()) return;
        var w = new Workspace("w", "Project", "/home/test/a b's; echo bad", "Debian");
        var start = Hosts.Agent(w, "codex-acp");
        Assert.Equal(w.Path, start.ArgumentList[3]);
        Assert.Equal("exec codex-acp", start.ArgumentList.Last());
        Assert.DoesNotContain(w.Path, start.ArgumentList.Last());
        Assert.Equal(Hosts.WindowsArgument(w.Path), TerminalSession.Options(w).CommandLine.Last());
    }
}
