using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class TranscriptScrollingTests
{
    [AvaloniaFact]
    public void BufferCountsConversationMessagesRatherThanThinkingAndActionGroups()
    {
        var messages = Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            new Message { Role = i % 2 == 0 ? "user" : "assistant", Text = "Message " + i },
            new Message { Role = "thought", Text = "Thinking" },
            new Message { Role = "tool", Text = "Action" },
            new Message { Role = "system", Text = "Session notice" }
        }).Append(new Message { Text = "Last message" }).ToArray();
        var list = Create(messages);
        list.ItemTemplate = new FuncDataTemplate<Message>((message, _) => new TextBlock { Text = message!.Text, Height = message == messages[^1] ? 900 : 100 });
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout(); var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.FollowEnd(); window.UpdateLayout();
            foreach (var index in new[] { 76, 72, 68, 64 }) Assert.NotNull(list.ContainerFromIndex(index));
            Assert.Null(list.ContainerFromIndex(60));
            Assert.Null(list.ContainerFromIndex(0));
            Assert.True(panel.IsFollowingEnd);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TallLastMessageKeepsPrecedingRowsWarmWithoutRealizingAllHistory()
    {
        var messages = Enumerable.Range(0, 100).Select(i => new Message { Sequence = i, Text = "Message " + i }).ToArray();
        var list = Create(messages);
        list.ItemTemplate = new FuncDataTemplate<Message>((message, _) => new TextBlock { Text = message!.Text, Height = message.Sequence == 99 ? 900 : 100 });
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout(); var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.FollowEnd(); window.UpdateLayout();
            var previous = list.ContainerFromIndex(98);
            Assert.NotNull(previous);
            Assert.NotNull(list.ContainerFromIndex(97));
            Assert.NotNull(list.ContainerFromIndex(96));
            Assert.NotNull(list.ContainerFromIndex(95));
            Assert.Null(list.ContainerFromIndex(94));
            Assert.Null(list.ContainerFromIndex(0));
            Assert.Equal(5, list.GetVisualDescendants().OfType<ListBoxItem>().Count());
            panel.Offset = new(0, panel.Offset.Y - 550); window.UpdateLayout();
            Assert.Same(previous, list.ContainerFromIndex(98));
            Assert.False(panel.IsFollowingEnd);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ShrinkingContentAtTheBottomDoesNotRequestOlderHistory()
    {
        var list = Create(Enumerable.Range(0, 8).Select(i => new Message { Sequence = i, Text = "Message " + i }));
        var loads = 0;
        _ = new TranscriptNavigation(list, new IconButton(), () => false, _ => { loads++; return Task.CompletedTask; });
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!; panel.FollowEnd(); window.UpdateLayout(); await Task.Delay(30);
            foreach (var block in list.GetVisualDescendants().OfType<TextBlock>()) block.Height = 1;
            window.UpdateLayout(); await Task.Delay(30);
            Assert.Equal(0, loads);
            Assert.True(panel.IsFollowingEnd);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BringingAnAlreadyVisibleMessageIntoViewDoesNotChangeTheReadingPosition()
    {
        var list = Create(Enumerable.Range(0, 100).Select(i => new Message { Sequence = i, Text = "Message " + i }));
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout(); var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.Offset = new(0, 500); window.UpdateLayout();
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First(c => c.Bounds.Top > 0 && c.Bounds.Bottom < panel.Viewport.Height);
            var offset = panel.Offset;
            panel.BringIntoView(row, new Rect(row.Bounds.Size)); window.UpdateLayout();
            Assert.Equal(offset, panel.Offset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BrowsingHistoryRetainsAllLoadedPages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-scroll-history", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "History", directory));
            var chat = new Chat { Id = "c", WorkspaceId = "w" }; store.Save(chat);
            for (var i = 0; i < 400; i++) store.SaveMessage(chat, new Message { Sequence = i, Text = "Message " + i });
        }
        var window = new MainWindow(); window.Show();
        try
        {
            var list = UiTests.Named<ListBox>(window, "MessageList");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (list.ItemCount < Chat.HistoryPageSize && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(Chat.HistoryPageSize, list.ItemCount);
            var browse = typeof(MainView).GetMethod("BrowseHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var i = 1; i <= 6; i++)
            {
                await (Task)browse.Invoke(window.View, [false])!;
                window.UpdateLayout();
                Assert.Equal(Chat.HistoryPageSize + 50 * i, list.ItemCount);
            }
            Assert.Equal(36, ((Message)list.Items[0]!).Sequence);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }

    private static ListBox Create(IEnumerable<Message> messages) => new()
    {
        ItemsSource = messages,
        ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()),
        ItemTemplate = new FuncDataTemplate<Message>((m, _) => new TextBlock { Text = m!.Text, Height = m.Sequence == -1 ? 10000 : 60 })
    };

    [AvaloniaFact]
    public void PrependingHistoryKeepsAnchorAndContainersAndGrowsTheScrollbarRange()
    {
        var messages = Enumerable.Range(0, 400).Select(i => new Message { Sequence = i, Text = "Message " + i }).ToArray();
        var list = Create(messages.Skip(336));
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            for (var first = 286; first >= 36; first -= 50)
            {
                panel.Offset = new(0, 40); window.UpdateLayout();
                var anchor = panel.CaptureAnchor();
                var container = list.GetVisualDescendants().OfType<ListBoxItem>().First(c => ReferenceEquals(c.DataContext, anchor!.Value.Item));
                var top = container.Bounds.Y;
                var extent = panel.Extent.Height;
                TranscriptNavigation.ReplacePage(list, messages.Skip(first).ToArray()); window.UpdateLayout();
                Assert.Equal(anchor, panel.CaptureAnchor());
                Assert.Contains(container, list.GetVisualDescendants().OfType<ListBoxItem>());
                Assert.Equal(top, container.Bounds.Y, 2);
                Assert.True(panel.Extent.Height > extent);
                Assert.False(panel.IsFollowingEnd);
                Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 20);
            }
            Assert.Equal(364, list.ItemCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RestoringInsideAnUnmeasuredTallRowKeepsTheExplicitAnchor()
    {
        var messages = new[] { new Message { Sequence = -1, Text = "Tall message" }, new Message { Sequence = 1, Text = "Next" } };
        var list = Create([]);
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout(); list.ItemsSource = messages;
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.RestoreAnchor((messages[0], 500)); window.UpdateLayout();
            Assert.Equal((messages[0], 500d), panel.CaptureAnchor());
            Assert.Equal(500, panel.Offset.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ScrollingPastATallMessageRealizesTheFollowingRows()
    {
        var messages = new[] { new Message { Sequence = -1, Text = "Tall message" } }.Concat(Enumerable.Range(0, 30).Select(i => new Message { Sequence = i, Text = "Next " + i })).ToArray();
        var list = Create(messages);
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.Offset = new(0, 9900); window.UpdateLayout();
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Contains(rows, c => ReferenceEquals(c.DataContext, messages[1]));
            Assert.True(rows.Max(c => c.Bounds.Bottom) >= panel.Viewport.Height);
            Assert.InRange(rows.Length, 2, 20);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AppendingPreservesVisibleControlsAndDoesNotFollowAfterScrollingAway()
    {
        var messages = new ObservableCollection<Message>(Enumerable.Range(0, 100).Select(i => new Message { Sequence = i, Text = "Message " + i }));
        var list = Create(messages);
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.FollowEnd(); window.UpdateLayout(); Assert.True(panel.IsFollowingEnd);
            panel.Offset = new(0, panel.Offset.Y - 32); window.UpdateLayout(); Assert.False(panel.IsFollowingEnd);
            var anchor = panel.CaptureAnchor();
            var visible = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            messages.Add(new() { Sequence = 100, Text = "New output" }); window.UpdateLayout();
            Assert.Equal(anchor, panel.CaptureAnchor()); Assert.False(panel.IsFollowingEnd);
            Assert.All(visible, c => Assert.Contains(c, list.GetVisualDescendants().OfType<ListBoxItem>()));
        }
        finally { window.Close(); }
    }
}
