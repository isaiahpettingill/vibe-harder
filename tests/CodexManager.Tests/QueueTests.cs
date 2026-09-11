using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class QueueTests
{
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
