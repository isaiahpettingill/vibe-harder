using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class StreamingTests
{
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
}
