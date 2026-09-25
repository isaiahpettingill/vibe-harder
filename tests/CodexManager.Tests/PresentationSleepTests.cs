using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class PresentationSleepTests
{
    [AvaloniaFact]
    public async Task BackgroundToolUpdatesKeepInputsAfterHistoryEviction()
    {
        var directory = Seed();
        using var store = new Store(directory, backgroundWrites: true);
        var workspace = store.Workspaces().Single();
        var chat = store.Chats().First(); chat.RetainHistory = false;
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Send("background-tools", []);
        Assert.Empty(chat.Messages);
        var saved = await store.ReadPageAsync(chat);
        Assert.Contains("echo first", saved.Single(m => m.ToolId == "one").Text);
        Assert.Contains("echo second", saved.Single(m => m.ToolId == "two").Text);
        Assert.Contains("first result", saved.Single(m => m.ToolId == "one").Text);
        Assert.Contains("second result", saved.Single(m => m.ToolId == "two").Text);
        Assert.All(saved.Where(m => m.ToolId is not null), m => Assert.Contains("completed", m.Text));
    }
    private static string Seed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-sleep", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using var store = new Store(directory);
        store.Save(new Workspace("w", "Sleep test", directory));
        foreach (var id in new[] { "a", "b" })
        {
            var chat = new Chat { Id = id, WorkspaceId = "w", Title = id }; store.Save(chat);
            for (var i = 0; i < 100; i++) store.SaveMessage(chat, new Message { Text = "Saved message " + i });
        }
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        return directory;
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(predicate());
    }
    [AvaloniaFact]
    public async Task SwitchingBackWithinGraceKeepsHistoryThenInactiveHistoryUnloads()
    {
        Seed(); var window = new MainWindow(); window.Show();
        try
        {
            var tabs = UiTests.Named<ListBox>(window, "Chats_w");
            var a = tabs.Items.OfType<Chat>().Single(c => c.Id == "a");
            var b = tabs.Items.OfType<Chat>().Single(c => c.Id == "b");
            tabs.SelectedItem = a; await Wait(() => a.HistoryLoaded);
            var cached = a.Messages.Last();
            tabs.SelectedItem = b; await Task.Delay(100);
            tabs.SelectedItem = a; Assert.Contains(cached, a.Messages);
            tabs.SelectedItem = b; await Wait(() => b.HistoryLoaded);
            await Wait(() => a.Messages.Count == 0);
            Assert.False(a.HistoryLoaded); Assert.Equal(Chat.HistoryPageSize, b.Messages.Count);
            tabs.SelectedItem = a; await Wait(() => a.HistoryLoaded);
            Assert.Equal("Saved message 99", a.Messages.Last().Text);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }
    [AvaloniaFact]
    public async Task SleepKeepsTheOlderPageBeingReadAndLatestCanReload()
    {
        Seed(); var window = new MainWindow(); window.Show();
        try
        {
            var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
            var list = UiTests.Named<ListBox>(window, "MessageList");
            await Wait(() => chat.HistoryLoaded);
            // Set up an older page through the same action as scroll pagination.
            // This test covers preserving that page across sleep, independently of layout timing.
            var browse = typeof(MainView).GetMethod("BrowseHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var page = 0; page < 4 && list.Items.OfType<Message>().FirstOrDefault()?.Sequence > 0; page++)
                await (Task)browse.Invoke(window.View, [false])!;
            await Wait(() => list.Items.OfType<Message>().FirstOrDefault()?.Sequence == 0);
            var pageEnd = list.Items.OfType<Message>().Last().Sequence;
            var pageCount = list.ItemCount;
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.Offset = new(0, 100); window.UpdateLayout();
            var anchor = panel.CaptureAnchor();
            await window.SetPresentationSleeping(true); await window.SetPresentationSleeping(false);
            await Task.Delay(50); window.UpdateLayout();
            Assert.Equal(pageCount, list.ItemCount);
            var restored = ((TranscriptPanel)list.ItemsPanelRoot!).CaptureAnchor();
            Assert.Equal((((Message)anchor!.Value.Item).Sequence, anchor.Value.Within), (((Message)restored!.Value.Item).Sequence, restored.Value.Within));
            Assert.Equal(anchor.Value.Within, restored.Value.Within, 2);
            Assert.Equal(pageEnd, list.Items.OfType<Message>().Last().Sequence);
            UiTests.Named<IconButton>(window, "HistoryNavigation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => list.Items.OfType<Message>().LastOrDefault()?.Sequence == 99);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }
    [AvaloniaFact]
    public async Task SleepingUnloadsTranscriptWhileAgentRunsAndWakeRestoresIt()
    {
        Seed(); var window = new MainWindow(); window.Show();
        try
        {
            var tabs = UiTests.Named<ListBox>(window, "Chats_w");
            var chat = (Chat)tabs.SelectedItem!; await Wait(() => chat.HistoryLoaded);
            window.FindControl<ComposerEditor>("Composer")!.Text = "hang";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Messages.Any(m => m.Text == "Working"));
            var spinner = window.GetVisualDescendants().OfType<ChatActivityIndicator>()
                .SelectMany(i => i.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()).First(p => p.IsVisible);
            await window.SetPresentationSleeping(true);
            var stoppedAngle = ((Avalonia.Media.RotateTransform)spinner.RenderTransform!).Angle;
            Assert.True(window.IsPresentationSleeping); Assert.True(chat.Busy);
            Assert.InRange(chat.Messages.Count, 0, 1);
            Assert.Empty(window.GetVisualDescendants().OfType<MessageView>());
            await Task.Delay(150); Assert.True(chat.Busy);
            Assert.Equal(stoppedAngle, ((Avalonia.Media.RotateTransform)spinner.RenderTransform!).Angle);
            // Escape now interrupts a running chat. Use a navigation key to test waking alone.
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Left });
            await Wait(() => !window.IsPresentationSleeping && chat.Messages.Count == Chat.HistoryPageSize);
            window.UpdateLayout();
            Assert.False(window.IsPresentationSleeping); Assert.True(chat.Busy);
            Assert.Equal(Chat.HistoryPageSize, chat.Messages.Count);
            Assert.Contains(chat.Messages, m => m.Text == "Working");
            Assert.Equal(chat.Messages.Count, chat.Messages.Select(m => m.Id).Distinct().Count());
        }
        finally { window.RequestExit(); await Task.Delay(250); }
    }
}
