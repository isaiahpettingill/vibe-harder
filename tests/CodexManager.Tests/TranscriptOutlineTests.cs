using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class TranscriptOutlineTests
{
    private const int Total = 1000;
    private static readonly Message[] Chat = Enumerable.Range(0, Total).Select(i => new Message { Id = "m" + i, Sequence = i, Role = i % 2 == 0 ? "user" : "assistant", Text = "Message " + i }).ToArray();
    private static readonly TranscriptOutline Outline = new("chat", Chat.Select(m => new OutlineEntry(m.Sequence, m.Role, m.Text.Length, 0)).ToArray());
    // Fresh objects, as a page read from the store or the host would be.
    private static Message[] Page(int from, int count) => Chat.Skip(from).Take(count).Select(m => new Message { Id = m.Id, Sequence = m.Sequence, Role = m.Role, Text = m.Text }).ToArray();
    private static ListBox Create(IEnumerable<Message> messages) => new()
    {
        ItemsSource = messages,
        ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()),
        ItemTemplate = new FuncDataTemplate<Message>((m, _) => new TextBlock { Text = m!.Text, Height = 60 })
    };

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public async Task ScrollbarSpansTheWholeChatAndDraggingLoadsThatPartWithoutJumping()
    {
        var list = Create(Page(Total - 64, 64));
        TranscriptPanel.SetOutline(list, Outline);
        var requests = new List<HistoryRequest>();
        var navigation = new TranscriptNavigation(list, new IconButton(), () => true, request =>
        {
            requests.Add(request);
            var visible = list.Items.OfType<Message>().ToArray();
            var page = request.Around is { } around ? Page(Math.Max(0, around - 32), 64)
                : request.Newer ? Page(visible[^1].Sequence + 1, 32) : Page(Math.Max(0, visible[0].Sequence - 32), 32);
            TranscriptNavigation.ReplacePage(list, request.Around is null ? HistoryWindow.Navigate(visible, page, request.Newer) : page);
            return Task.CompletedTask;
        });
        var window = new Window { Content = list, Width = 600, Height = 400 }; window.Show();
        try
        {
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.FollowEnd(); window.UpdateLayout();
            var scroll = list.Scroll!;
            // Only 64 of 1000 rows are loaded, yet the scrollbar spans the whole chat.
            Assert.InRange(scroll.Extent.Height, Total * 60 * 0.8, Total * 60 * 1.25);
            Assert.InRange(scroll.Offset.Y, scroll.Extent.Height - scroll.Viewport.Height - 1, scroll.Extent.Height);

            // Scrolling up asks for older messages while loaded ones are still on screen.
            for (var step = 0; step < 40 && requests.Count == 0; step++) { scroll.Offset = new Vector(0, scroll.Offset.Y - 150); window.UpdateLayout(); await Task.Delay(10); }
            Assert.Contains(requests, r => !r.Newer && r.Around is null);
            Assert.NotEmpty(list.GetRealizedContainers());
            // Let that page load settle; scrolling while a page is still loading is ignored.
            for (var i = 0; i < 5; i++) { await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background); window.UpdateLayout(); }

            // Dragging the thumb to the middle loads the messages there, and the view stays put.
            var middle = scroll.Extent.Height / 2;
            scroll.Offset = new Vector(0, middle); window.UpdateLayout();
            for (var wait = 0; wait < 100 && !requests.Any(r => r.Around is not null); wait++) await Task.Delay(20);
            window.UpdateLayout();
            var around = Assert.Single(requests, r => r.Around is not null).Around!.Value;
            Assert.InRange(around, Total / 2 - 100, Total / 2 + 100);
            Assert.InRange(scroll.Offset.Y, middle - scroll.Viewport.Height, middle + scroll.Viewport.Height);
            var shown = list.GetRealizedContainers().Select(c => c.DataContext).OfType<Message>().Select(m => m.Sequence).ToArray();
            Assert.Contains(shown, s => Math.Abs(s - around) < 40);
        }
        finally { window.Close(); }
    }
}
