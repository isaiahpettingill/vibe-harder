using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class StreamingTests
{
    [Trait("Category", "Integration")]
    [AvaloniaTheory]
    [InlineData("usage-report")]
    [InlineData("usage-report-late")]
    public async Task TurnAndLateUsageAreSavedWithoutEstimatingCost(string prompt)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("usage-flow-").FullName);
        var workspace = new Workspace("w", "Fixture", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, Provider = AgentProvider.Claude }; store.Save(chat);
        var fixture = System.IO.Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
        var command = OperatingSystem.IsWindows() ? "node '" + fixture.Replace("'", "''") + "'" : "node " + Hosts.Quote(fixture);
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        await runtime.Send(prompt, []).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (ChatUsage.Currencies(store).Length == 0 && DateTime.UtcNow < until) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Equal(1.25m, Assert.Single(ChatUsage.Daily(store, "USD")).Cost);
        using var state = System.Text.Json.JsonDocument.Parse(store.Setting("usage:" + chat.Id)!);
        Assert.Equal(123, state.RootElement.GetProperty("used").GetInt32());
        Assert.Equal(500, state.RootElement.GetProperty("totalTokens").GetInt32());
        Assert.False(chat.Busy);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("gh issue create --title bug", "completed", 1)]
    [InlineData("gh issue edit 12 --repo org/repo --title fix", "completed", 1)]
    [InlineData("gh issue edit 12", "failed", 0)]
    public async Task SuccessfulIssueMutationsAreSavedFromStreamedUpdates(string command, string status, int expected)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("issue-stream-").FullName);
        var workspace = new Workspace("w", "Project", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "unused");
        var update = typeof(ChatRuntime).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(System.Text.Json.JsonElement)])!;
        foreach (var packet in new[] {
            new System.Text.Json.Nodes.JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "issue", ["status"] = "in_progress", ["rawInput"] = new System.Text.Json.Nodes.JsonObject { ["command"] = command } },
            new System.Text.Json.Nodes.JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "issue", ["status"] = status, ["rawOutput"] = "https://github.com/org/repo/issues/12" }
        }) await (Task)update.Invoke(runtime, [System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(packet.ToJsonString())])!;
        var restored = new Chat { Id = chat.Id, WorkspaceId = workspace.Id };
        ChatIssues.Load(store, restored);
        Assert.Equal(expected, restored.Issues.Count);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData(false, "completed", 1)]
    [InlineData(false, "failed", 0)]
    [InlineData(true, "completed", 0)]
    public async Task PullRequestCelebrationFiresOnceForLiveSuccessfulCreation(bool replay, string status, int expected)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("pr-celebration-").FullName);
        var workspace = new Workspace("w", "Project", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "unused");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ChatRuntime).GetField("replaying", flags)!.SetValue(runtime, replay);
        var update = typeof(ChatRuntime).GetMethod("Update", flags, [typeof(System.Text.Json.JsonElement)])!;
        var count = 0; runtime.PullRequestCreated += () => count++;
        foreach (var packet in new[] {
            """{"sessionUpdate":"tool_call","toolCallId":"pr","status":"in_progress","rawInput":{"command":"gh pr create --fill"}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"pr","rawOutput":"https://github.com/org/repo/pull/12"}""",
            new System.Text.Json.Nodes.JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "pr", ["status"] = status }.ToJsonString(),
            new System.Text.Json.Nodes.JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "pr", ["status"] = status }.ToJsonString()
        }) await (Task)update.Invoke(runtime, [System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(packet)])!;
        Assert.Equal(expected, count);
    }

    [Trait("Category", "CI")]
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

    [Trait("Category", "Integration")]
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
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
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
    }
    [Trait("Category", "CI")]
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
        Assert.Contains(chat.Messages, m => m.Role == "system");
        Assert.Contains("Earlier request", File.ReadAllText(prompts));
        Assert.Contains("Keep queued", File.ReadAllText(prompts));
        Assert.Single(File.ReadAllLines(log));
    }
    [Trait("Category", "Integration")]
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
