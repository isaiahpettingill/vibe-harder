using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class StreamingTests
{
    [Theory]
    [InlineData(AgentProvider.Claude, 1)]
    [InlineData(AgentProvider.Codex, 3)]
    public async Task ClaudeChunksWithFlippingMessageIdsStayOneMessage(AgentProvider provider, int expected)
    {
        // Concurrent Claude subagents retag the main reply's chunks with their own message ids.
        using var store = new Store(Directory.CreateTempSubdirectory("chunk-ids-").FullName);
        var workspace = new Workspace("w", "Chunks", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = provider }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "unused");
        var update = typeof(ChatRuntime).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(System.Text.Json.JsonElement)])!;
        foreach (var (id, text) in new[] { ("msg_main", "Here's where the "), ("msg_sub1", "Go tests stand: "), ("msg_sub2", "all pass.") })
            await (Task)update.Invoke(runtime, [System.Text.Json.JsonSerializer.SerializeToElement(new System.Text.Json.Nodes.JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = id,
                ["content"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text }
            })])!;
        var replies = chat.Messages.Where(m => m.Role == "assistant").ToArray();
        Assert.Equal(expected, replies.Length);
        Assert.Equal("Here's where the Go tests stand: all pass.", string.Concat(replies.Select(m => m.Text)));
        // The latest id is kept so history points still resolve.
        Assert.Equal("msg_sub2", replies[^1].ProviderMessageId);
    }

    [AvaloniaFact]
    public void IconButtonsUseRealButtonTemplateAndMouseHitArea()
    {
        var button = new IconButton { Icon = "send", Label = "Send", Width = 36, Height = 36 };
        var window = new Window { Content = button, Width = 100, Height = 100 }; window.Show();
        try
        {
            var clicked = 0; button.Click += (_, _) => clicked++;
            Assert.NotNull(button.Template);
            var point = button.TranslatePoint(new Point(3, 3), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, clicked);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task PartialAnswerIsRenderedWhilePromptStillRunning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-stream", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Streaming", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var messages = new StackPanel(); var window = new Window { Content = messages }; window.Show();
        chat.Messages.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (Message message in e.NewItems) messages.Children.Add(new MessageView { Message = message }); };
        try
        {
            var sending = runtime.Send("stream", []);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!chat.Messages.Any(m => m.Text == "First partial answer") && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.False(sending.IsCompleted); Assert.True(chat.Busy);
            // ChatMarkdown renders on a short timer, so wait for the visual rather than racing it.
            while (!messages.GetVisualDescendants().OfType<ChatMarkdown>().Any(m => m.Text == "First partial answer") && DateTime.UtcNow < until && !sending.IsCompleted) await Task.Delay(20);
            Assert.Contains(messages.GetVisualDescendants().OfType<ChatMarkdown>(), m => m.Text == "First partial answer");
            await sending; Assert.Equal("First partial answer and final answer", chat.Messages.Last().Text);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingSessionOnlyRecoversIfKnownNeverPrompted(bool unmaterialized)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-missing", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Missing", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", SessionId = "missing-empty", Draft = "Keep my draft" }; store.Save(chat);
        if (unmaterialized) store.Setting("unmaterialized:" + chat.Id, chat.SessionId);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        if (unmaterialized) { await runtime.Connect(); Assert.Equal("fixture-session", chat.SessionId); }
        else { var error = await Assert.ThrowsAsync<AcpException>(() => runtime.Connect()); Assert.Contains("no rollout found", error.Message); Assert.Equal("missing-empty", chat.SessionId); }
        Assert.Equal("Keep my draft", chat.Draft); Assert.Empty(chat.Messages);
    }
    [AvaloniaFact]
    public async Task MissingRolloutWithoutFoundRecoversAnUnpromptedSession()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-short-rollout-").FullName);
        var workspace = new Workspace("w", "Missing", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, SessionId = "missing-short", Draft = "Keep my draft" }; store.Save(chat);
        store.Setting("unmaterialized:" + chat.Id, chat.SessionId);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Reconnect();
        Assert.True(runtime.IsConnected);
        Assert.Equal("fixture-session", chat.SessionId);
        Assert.Equal("Keep my draft", chat.Draft);
        Assert.Equal("Ready", chat.Status);
    }
    [AvaloniaFact]
    public async Task MissingPromptedSessionUsesSavedTranscriptAndSendsQueuedInput()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-missing-prompted-").FullName);
        var workspace = new Workspace("w", "Missing", store.DirectoryPath); store.Save(workspace);
        var log = Path.Combine(store.DirectoryPath, "starts.txt");
        var prompts = Path.Combine(store.DirectoryPath, "prompts.txt");
        var chat = new Chat { WorkspaceId = workspace.Id, SessionId = "missing-short" }; store.Save(chat);
        var prior = new Message { Role = "user", Text = "Earlier request" }; chat.Messages.Add(prior); store.SaveMessage(chat, prior);
        chat.QueuedInputs.Add(new PendingInput("Keep queued", [])); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --startup-log=\"" + log + "\" --prompt-log=\"" + prompts + "\"");
        await runtime.Reconnect();
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((chat.QueuedInputs.Count > 0 || chat.Busy) && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(runtime.IsConnected);
        Assert.Equal("fixture-session", chat.SessionId);
        Assert.Empty(chat.QueuedInputs);
        Assert.Contains(chat.Messages, m => m.Role == "system" && m.Text.Contains("replacement session"));
        Assert.Contains("Earlier request", File.ReadAllText(prompts));
        Assert.Contains("Keep queued", File.ReadAllText(prompts));
        Assert.Single(File.ReadAllLines(log));
    }
    [AvaloniaFact]
    public async Task ReplacementKeepsTranscriptContextAcrossRestartBeforeNextPrompt()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-replacement-context-").FullName);
        var workspace = new Workspace("w", "Missing", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, SessionId = "missing-short" }; store.Save(chat);
        var prior = new Message { Role = "user", Text = "Earlier request" }; chat.Messages.Add(prior); store.SaveMessage(chat, prior);
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"";
        await using (var first = new ChatRuntime(chat, workspace, store, command)) await first.Connect();
        var prompts = Path.Combine(store.DirectoryPath, "prompts.txt");
        await using (var second = new ChatRuntime(chat, workspace, store, command + " --prompt-log=\"" + prompts + "\"")) await second.Send("Next request", []);
        Assert.Contains("Earlier request", File.ReadAllText(prompts));
        Assert.Contains("Next request", File.ReadAllText(prompts));
        Assert.Equal("", store.Setting("restoreContext:" + chat.Id));
    }
}
