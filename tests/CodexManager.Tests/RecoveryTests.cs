using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class RecoveryTests
{
    [Fact]
    public void RecoveryKeepsNewDraftAlongsideInterruptedRequest()
    {
        var chat = new Chat { WorkspaceId = "w", Draft = "A new unsent thought" };
        chat.RecoverInput(new("Original interrupted request", []));
        Assert.StartsWith("A new unsent thought", chat.Draft);
        Assert.Contains("[Recovered interrupted request]\nOriginal interrupted request", chat.Draft);
    }
    private static string Quote(string path) => OperatingSystem.IsWindows() ? "'" + path.Replace("'", "''") + "'" : Hosts.Quote(path);
    private static string ReadLog(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    [AvaloniaTheory]
    [InlineData("disconnect")]
    [InlineData("idle-exit")]
    public async Task RecoversServerExitWithoutResendingPrompt(string prompt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-recovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "recovery.txt");
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Recovery", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Claude }; store.Save(chat);
        var command = "node " + Quote(Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs")) + " " + Quote("--recovery=" + log);
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        runtime.IsActiveView = () => true;
        await runtime.Send(prompt, []).WaitAsync(TimeSpan.FromSeconds(15));
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && (!File.Exists(log) || !ReadLog(log).Contains("loaded") || chat.Busy)) await Task.Delay(30);
        Assert.Contains("loaded", ReadLog(log));
        Assert.Equal(1, ReadLog(log).Split('\n').Count(l => l == "prompt"));
        Assert.Equal("fixture-session", chat.SessionId);
        Assert.False(chat.Busy);
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("REPLAY"));
        if (prompt == "disconnect")
        {
            Assert.Contains(chat.Messages, m => m.Text == "Partial response");
            Assert.Equal(prompt, chat.Draft);
        }
        await runtime.Send("next turn", []);
        Assert.Equal("Hello **world**", chat.Messages.Last().Text);
    }
    [AvaloniaFact]
    public async Task RetriesStartupAndReceivesAuthenticationState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-recovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Recovery", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var log = Path.Combine(directory, "attempts.txt");
        var command = "node " + Quote(Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs")) + " --startup-failure " + Quote("--recovery=" + log);
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        await runtime.Send("auth", []).WaitAsync(TimeSpan.FromSeconds(15));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!chat.NeedsLogin && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(chat.NeedsLogin);
        Assert.Equal(1, ReadLog(log).Split('\n').Count(l => l == "startup"));
        Assert.Equal(1, ReadLog(log).Split('\n').Count(l => l == "prompt"));
    }
    [Fact]
    public void AppRestartRecoversPendingInputAttachmentsAndProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-recovery", Guid.NewGuid().ToString("N"));
        var attachment = new Attachment("file.txt", "text/plain", "content", Path.GetFullPath("file.txt"));
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Recovery", Path.GetTempPath()));
            var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.OpenCode, SessionId = "opaque-opencode-id", PendingInput = new("Unfinished request", [attachment]) };
            store.Save(chat); var message = new Message { Text = "Partial reply", Provider = AgentProvider.OpenCode }; chat.Messages.Add(message); store.SaveMessage(chat, message);
        }
        using var reopened = new Store(directory);
        var restored = Assert.Single(reopened.Chats()); reopened.LoadMessages(restored);
        Assert.Equal(AgentProvider.OpenCode, restored.Provider); Assert.Equal("opaque-opencode-id", restored.SessionId);
        Assert.Equal("Unfinished request", restored.Draft); Assert.Equal(attachment, Assert.Single(restored.Attachments));
        Assert.Equal("Partial reply", Assert.Single(restored.Messages).Text); Assert.Equal("OPENCODE", restored.Messages[0].Label);
        Assert.False(restored.Busy); Assert.Null(restored.PendingInput);
    }
}
