namespace CodexManager.Tests;

public class StoreTests
{
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void UsageSnapshotsPersistWithoutCountingRepeatedOrReplayedCosts(bool replayFirst, int expectedDailyCost)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("usage-").FullName);
        var chat = new Chat { Id = "chat", WorkspaceId = "workspace", SessionId = "session" };
        var day = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
        System.Text.Json.JsonElement Packet(int cost) => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"used\":100,\"size\":1000,\"cost\":{\"amount\":" + cost + ",\"currency\":\"USD\"}}");
        ChatUsage.Apply(store, chat, Packet(1), replayFirst, day);
        ChatUsage.Apply(store, chat, Packet(1), false, day);
        ChatUsage.Apply(store, chat, Packet(3), false, day);
        ChatUsage.Apply(store, chat, Packet(2), false, day);
        Assert.Equal(expectedDailyCost, Assert.Single(ChatUsage.Daily(store, "USD")).Cost);
        var restored = new Chat { Id = chat.Id, WorkspaceId = chat.WorkspaceId };
        ChatUsage.Load(store, restored);
        Assert.Equal(chat.CostLabel, restored.CostLabel);
        Assert.Equal(chat.ContextLabel, restored.ContextLabel);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("{\"used\":123}", true)]
    [InlineData("{\"totalTokens\":456,\"inputTokens\":400,\"outputTokens\":56}", true)]
    [InlineData("{\"used\":null,\"cost\":null}", false)]
    [InlineData("{\"used\":-1,\"cost\":{\"amount\":-1,\"currency\":\"USD\"}}", false)]
    public void TokenOnlyUsageDoesNotInventCost(string json, bool expectedTokens)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("usage-tokens-").FullName);
        var chat = new Chat { WorkspaceId = "workspace" };
        ChatUsage.Apply(store, chat, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json), false, DateTimeOffset.UtcNow);
        Assert.Equal(expectedTokens, chat.ContextLabel.Length > 0);
        Assert.Empty(chat.CostLabel); Assert.Empty(ChatUsage.Currencies(store));
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(301, true)]
    public void GitHubRefreshesAreLimitedPerItem(int seconds, bool expected)
    {
        var schedule = new GitHubRefreshSchedule();
        var start = DateTimeOffset.UnixEpoch;
        Assert.True(schedule.TryStart("https://github.com/org/repo/pull/1", start));
        Assert.Equal(expected, schedule.TryStart("https://github.com/org/repo/pull/1", start.AddSeconds(seconds)));
        Assert.True(schedule.TryStart("https://github.com/org/repo/pull/2", start.AddSeconds(seconds)));
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("OPEN", true, false)]
    [InlineData("CLOSED", true, true)]
    [InlineData("CLOSED", false, false)]
    public void TrackedIssuesPersistAndDismissOnlyWhenClosed(string state, bool dismiss, bool expectedDismissed)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("issues-").FullName);
        var chat = new Chat { Id = "chat", WorkspaceId = "workspace" };
        const string url = "https://github.com/org/repo/issues/12";
        ChatIssues.Add(store, chat, url); ChatIssues.Add(store, chat, url);
        ChatIssues.Add(store, chat, "https://github.com/org/repo/pull/12");
        ChatIssues.Apply(store, chat, url, "Updated title", state);
        if (dismiss) ChatIssues.Dismiss(store, chat, url);
        var restored = new Chat { Id = chat.Id, WorkspaceId = chat.WorkspaceId };
        ChatIssues.Load(store, restored);
        var issue = Assert.Single(restored.Issues);
        Assert.Equal("Updated title", issue.Title); Assert.Equal(state, issue.State); Assert.Equal(expectedDismissed, issue.Dismissed);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("OPEN", true, 1)]
    [InlineData("CLOSED", false, 1)]
    [InlineData("CLOSED", true, 0)]
    [InlineData("MERGED", false, 0)]
    [InlineData("MERGED", true, 0)]
    public void PullRequestStateAndDismissalPersist(string state, bool dismiss, int expected)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("chat-prs-").FullName);
        var chat = new Chat { Id = "chat", WorkspaceId = "workspace" };
        const string url = "https://github.com/org/repo/pull/12";
        ChatPullRequests.Add(store, chat, url);
        ChatPullRequests.Add(store, chat, url);
        ChatPullRequests.Add(store, chat, "https://evil.example/pr; bad-command");
        ChatPullRequests.Apply(store, chat, url, "Feature", state);
        if (dismiss) ChatPullRequests.Dismiss(store, chat, url);
        var restored = new Chat { Id = chat.Id, WorkspaceId = chat.WorkspaceId };
        ChatPullRequests.Load(store, restored);
        Assert.Single(restored.PullRequests);
        Assert.Equal(expected, restored.OpenPullRequestCount);
        Assert.Equal("Feature", restored.PullRequests[0].Title);
        Assert.Equal(dismiss && state != "OPEN", restored.PullRequests[0].Dismissed);
        if (state == "CLOSED" && dismiss)
        {
            ChatPullRequests.Add(store, restored, url);
            Assert.Equal(0, restored.OpenPullRequestCount);
            ChatPullRequests.Apply(store, restored, url, "Feature", "OPEN");
            Assert.Equal(1, restored.OpenPullRequestCount);
        }
    }

    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("/home/test/a b's; echo bad")]
    [InlineData("/home/test/plain")]
    [InlineData("/home/test/$(echo bad)")]
    public void WslArgumentsKeepPathAsOneArgument(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var w = new Workspace("w", "Project", path, "Debian");
        var start = Hosts.Agent(w, "codex-acp");
        Assert.Equal(w.Path, start.ArgumentList[3]);
        Assert.EndsWith("exec codex-acp", start.ArgumentList.Last());
        Assert.DoesNotContain(w.Path, start.ArgumentList.Last());
        var terminal = TerminalSession.Options(w);
        Assert.Equal("--cd", terminal.CommandLine[2]);
        Assert.Equal(Hosts.WindowsArgument(w.Path), terminal.CommandLine[3]);
        Assert.DoesNotContain(w.Path, terminal.CommandLine.Last());
    }
}
