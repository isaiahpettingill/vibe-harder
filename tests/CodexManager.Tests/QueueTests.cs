using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class QueueTests
{
    [AvaloniaFact]
    public async Task EscapeInterruptsWithAllQueuedMessagesAndAttachments()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("interrupt-queue-").FullName);
        var workspace = new Workspace("w", "Queue", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --steering");
        var original = runtime.Send("hang", []);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!chat.Messages.Any(m => m.Text == "Working") && DateTime.UtcNow < until) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(runtime.IsPrompting);
        var attachment = new Attachment("notes.txt", "text/plain", "notes");
        runtime.Queue(new("First queued", [attachment])); runtime.Queue(new("Second queued", []));
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        await service.Handle(new() { ["method"] = "queue/interrupt", ["chatId"] = chat.Id });
        Assert.True(original.IsCompleted); Assert.Empty(chat.QueuedInputs);
        var sent = Assert.Single(chat.Messages, m => m.Role == "user" && m.Text.StartsWith("First queued\n\nSecond queued"));
        Assert.Equal(attachment, Assert.Single(sent.Attachments));
        await runtime.Stop();
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyEnterSubmitsWholeQueueAndFallsBackToInterrupt(bool steering)
    {
        var directory = Directory.CreateTempSubdirectory("advance-queue-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Queue", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"" + (steering ? " --steering" : ""));
        var original = runtime.Send("hang", []);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!chat.Messages.Any(m => m.Text == "Working") && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(runtime.IsPrompting);
        runtime.Queue(new("hang", [])); runtime.Queue(new("still queued", []));
        await runtime.AdvanceQueued();
        Assert.Empty(chat.QueuedInputs);
        Assert.Contains(chat.Messages, m => m.Role == "user" && m.Text == "hang\n\nstill queued");
        Assert.Equal(steering, !original.IsCompleted);
        await runtime.Stop();
    }
    [AvaloniaFact]
    public async Task QueueDrainsAfterCompletionButSurvivesStopAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-queue", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Queue", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var sending = runtime.Send("stream", []);
        while (!chat.Messages.Any(m => m.Text == "First partial answer")) await Task.Delay(20);
        runtime.Queue(new("afterwards", []));
        Assert.DoesNotContain(chat.Messages, m => m.Text == "afterwards");
        await sending;
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((!chat.Messages.Any(m => m.Text == "afterwards") || chat.Busy) && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Contains(chat.Messages, m => m.Text == "afterwards"); Assert.Empty(chat.QueuedInputs);
        var hanging = runtime.Send("hang", []);
        while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20);
        runtime.Queue(new("keep queued", [])); await runtime.Stop(); await hanging;
        Assert.Single(chat.QueuedInputs); Assert.DoesNotContain(chat.Messages, m => m.Text == "keep queued");
        using var reopened = new Store(directory); Assert.Equal("keep queued", reopened.Chats().Single().QueuedInputs.Single().Text);
        Assert.Null(reopened.Chats().Single().InterruptedInput);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteeringUsesAdvertisedExtensionWithoutCancellingTurn(bool detached)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-steer", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Steer", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --steering" + (detached ? " --detached" : ""));
        var sending = runtime.Send("hang", []);
        while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20);
        Assert.True(runtime.SupportsSteering);
        Assert.True(await runtime.Steer(new("new direction", [])));
        if (!detached) Assert.False(sending.IsCompleted);
        Assert.True(chat.Busy); Assert.True(runtime.IsPrompting);
        Assert.Contains(chat.Messages, m => m.Role == "user" && m.Text == "new direction");
        await runtime.Stop(); await sending;
    }
}
