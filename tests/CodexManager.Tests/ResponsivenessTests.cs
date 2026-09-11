using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

public class ResponsivenessTests
{
    [AvaloniaFact]
    public async Task HistoryReplayKeepsVisiblePageStableAndSettlesAfterCompletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-replay-layout", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Replay", directory));
            store.Save(new Chat { Id = "c", WorkspaceId = "w", SessionId = "many" });
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --load-many");
        }
        var window = new MainWindow(); window.Show();
        try
        {
            var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
            var list = UiTests.Named<ListBox>(window, "MessageList");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (chat.Messages.Count < Chat.HistoryPageSize && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(chat.Busy);
            Assert.NotSame(chat.Messages, list.ItemsSource);
            Assert.Empty(list.Items);
            deadline = DateTime.UtcNow.AddSeconds(45);
            while (chat.Busy && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.False(chat.Busy); Assert.Same(chat.Messages, list.ItemsSource);
            Assert.Equal(Chat.HistoryPageSize, list.ItemCount);
            for (var frame = 0; frame < 25; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var offsets = new List<double>();
            for (var i = 0; i < 10; i++) { await Task.Delay(50); window.UpdateLayout(); offsets.Add(scroll.Offset.Y); }
            Assert.True(offsets.Max() - offsets.Min() < 1, string.Join(",", offsets));
            Assert.True(list.GetVisualDescendants().OfType<MessageView>().Any(v => v.Message == chat.Messages[^1]), "Last " + chat.Messages[^1].Sequence + " visible " + string.Join(",", list.GetVisualDescendants().OfType<MessageView>().Select(v => v.Message?.Sequence)));
            Assert.InRange(list.GetVisualDescendants().OfType<MessageView>().Count(), 1, 40);
            chat.Messages[^1].Text += string.Concat(Enumerable.Repeat("\n\nStreaming another paragraph.", 20));
            for (var frame = 0; frame < 10; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            Assert.InRange(Math.Abs(scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y), 0, 1);
            scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y - 600));
            for (var frame = 0; frame < 10; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            var readingOffset = scroll.Offset.Y;
            chat.Messages[^1].Text += "\n\nAnother streamed paragraph while reading earlier text.";
            for (var frame = 0; frame < 10; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            Assert.InRange(Math.Abs(scroll.Offset.Y - readingOffset), 0, 1);
            window.Width = 450;
            for (var frame = 0; frame < 10; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            var settled = scroll.Offset.Y;
            for (var frame = 0; frame < 10; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            Assert.InRange(Math.Abs(scroll.Offset.Y - settled), 0, 1);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }

    [Fact]
    public async Task BackgroundWritesRemainOrderedAndDoNotBlockOnLockedDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-writer", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory, backgroundWrites: true);
        store.Save(new Workspace("w", "Test", directory));
        var chat = new Chat { Id = "c", WorkspaceId = "w" }; store.Save(chat); await store.FlushAsync();
        using var blocker = new SqliteConnection($"Data Source={Path.Combine(directory, "sessions.db")}"); blocker.Open();
        using (var transaction = blocker.BeginTransaction())
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            chat.Archived = true; store.Save(chat); store.Setting("runInTray", "1");
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.Equal("1", store.Setting("runInTray"));
            var flush = store.FlushAsync(); await Task.Delay(100, TestContext.Current.CancellationToken); Assert.False(flush.IsCompleted);
            transaction.Commit(); await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        using var verify = new Store(directory); Assert.True(verify.Chats().Single().Archived);
    }

    [AvaloniaFact]
    public async Task LongTranscriptRealizesOnlyVisibleMessagesAndRecyclesWhileScrolling()
    {
        var messages = Enumerable.Range(0, Chat.HistoryPageSize).Select(i => new Message { Text = "Message " + i + "\n\nSome **formatted** content." }).ToArray();
        var list = new ListBox { ItemsSource = messages, ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()), ItemTemplate = new FuncDataTemplate<Message>((m, _) => { var view = new MessageView(); view.Bind(MessageView.MessageProperty, new Avalonia.Data.Binding()); return view; }, true) };

        var window = new Window { Width = 600, Height = 400, Content = list }; window.Show();
        try
        {
            window.UpdateLayout(); await Task.Delay(100);
            Assert.InRange(list.GetVisualDescendants().OfType<MessageView>().Count(), 1, 40);
            list.ScrollIntoView(messages[^1]);
            for (var frame = 0; frame < 25; frame++) { window.UpdateLayout(); await Task.Delay(20); }
            Assert.True(list.GetVisualDescendants().OfType<MessageView>().Any(v => v.Message == messages[^1]), "Visible " + string.Join(",", list.GetVisualDescendants().OfType<MessageView>().Select(v => Array.IndexOf(messages, v.Message))));
            Assert.InRange(list.GetVisualDescendants().OfType<MessageView>().Count(), 1, 40);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletedReplyPersistsButCancelledTurnDoesNotMarkUnread()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-unread", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory); var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Send("hello", []); Assert.True(chat.HasUnreadCompletion); Assert.True(store.Chats().Single().HasUnreadCompletion);
        var pending = runtime.Send("hang", []);
        while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20);
        await runtime.Stop(); await pending; Assert.False(chat.HasUnreadCompletion);
    }
    [Fact]
    public async Task HistoryPagesRemainBoundedAndFullCopyIncludesOlderMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-pages", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory, backgroundWrites: true);
        store.Save(new Workspace("w", "Pages", directory)); var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        for (var i = 0; i < 1500; i++) { var message = new Message { Text = "Saved message " + i }; chat.Messages.Add(message); store.SaveMessage(chat, message); store.TrimHistory(chat); }
        Assert.Equal(Chat.HistoryPageSize, chat.Messages.Count);
        var page = await store.ReadPageAsync(chat, token: TestContext.Current.CancellationToken); Assert.Equal(1500 - Chat.HistoryPageSize, page[0].Sequence); Assert.Equal(1499, page[^1].Sequence);
        var older = await store.ReadPageAsync(chat, page[0].Sequence, token: TestContext.Current.CancellationToken); Assert.Equal(1500 - 2 * Chat.HistoryPageSize, older[0].Sequence); Assert.Equal(1499 - Chat.HistoryPageSize, older[^1].Sequence);
        var newer = await store.ReadPageAsync(chat, older[^1].Sequence, token: TestContext.Current.CancellationToken, newer: true); Assert.Equal(1500 - Chat.HistoryPageSize, newer[0].Sequence);
        var copied = await store.ExportChatAsync(chat); Assert.Contains("Saved message 0\n", copied.Plain); Assert.Contains("Saved message 1499", copied.Html);
    }

    [AvaloniaFact]
    public async Task ArchiveCancelsHistoryLoadAndTrayDoesNotCountItAsRunningAgent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-archive", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Archive", directory)); store.Save(new Chat { Id = "c", WorkspaceId = "w", SessionId = "history-session" });
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --load-hang");
        }
        var window = new MainWindow(); window.Show();
        async Task Wait(Func<bool> predicate) { var until = DateTime.UtcNow.AddSeconds(5); while (!predicate() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(predicate()); }
        try
        {
            var list = UiTests.Named<ListBox>(window, "Chats_w"); var chat = (Chat)list.SelectedItem!;
            await Wait(() => chat.Busy && chat.Messages.Count > 0);
            var tray = (TrayIcon)typeof(MainWindow).GetField("tray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            Assert.Contains("no agents running", tray.ToolTipText);
            UiTests.Named<Button>(window, "CollapseWorkspace_w").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.False(list.IsVisible);
            UiTests.Named<Button>(window, "CollapseWorkspace_w").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.True(list.IsVisible);
            window.FindControl<Button>("ArchiveChatButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(chat.Archived); Assert.Empty(list.Items);
            await Wait(() => !chat.Busy && chat.Messages.Count == 0);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }

}







