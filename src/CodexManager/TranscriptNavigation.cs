using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CodexManager;

// Older or newer messages next to the loaded ones, or the page around a sequence the
// scrollbar was dragged to.
public readonly record struct HistoryRequest(bool Newer, int? Around = null);

public sealed class TranscriptNavigation
{
    private readonly ListBox list;
    private readonly IconButton latest;
    private readonly Func<bool> history;
    private readonly Func<HistoryRequest, Task> browse;
    private bool paging;
    public TranscriptNavigation(ListBox list, IconButton latest, Func<bool> history, Func<HistoryRequest, Task> browse)
    {
        this.list = list; this.latest = latest; this.history = history; this.browse = browse;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, Changed, RoutingStrategies.Bubble);
        list.AddHandler(InputElement.PointerWheelChangedEvent, async (_, e) =>
        {
            if (list.Scroll is not { } scroll) return;
            // Held at the edge of the loaded messages, the offset stops changing; keep paging.
            if (list.ItemsPanelRoot is TranscriptPanel { IsFollowingEnd: false } panel && panel.Needs is var needs)
            {
                if (e.Delta.Y > 0 && needs.Older) { await Load(new(false)); return; }
                if (e.Delta.Y < 0 && needs.Newer) { await Load(new(true)); return; }
            }
            if (e.Delta.Y > 0 && scroll.Offset.Y < 64) await Load(new(false));
            else if (e.Delta.Y < 0 && history() && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 64) await Load(new(true));
        }, RoutingStrategies.Tunnel);
    }
    public void Update()
    {
        var scroll = list.Scroll;
        latest.IsVisible = list.IsVisible && list.ItemCount > 0 && (history() || scroll is not null && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y > Math.Max(300, scroll.Viewport.Height * .75));
    }
    private async void Changed(object? sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, list.Scroll)) return;
        Update();
        // Measuring markdown and resizing the composer also change the offset.
        // Those adjustments must never turn a live transcript into a history page.
        if (paging || list.Scroll is not { } scroll || e.OffsetDelta.Y == 0 || e.ViewportDelta != default) return;
        // Rows scrolled into view are measured and correct their estimated heights, so the extent
        // changes along with the offset; that is still the user scrolling.
        if (list.ItemsPanelRoot is TranscriptPanel { IsFollowingEnd: false, Scrolled: true } panel && panel.Needs is var needs)
        {
            panel.Scrolled = false;
            // The scrollbar spans the whole chat: load what is about to come into view, or what the
            // thumb was dragged to, ahead of time instead of at the edge of the loaded messages.
            if (needs.Around is { } around) { await Load(new(false, around)); return; }
            if (needs.Older && e.OffsetDelta.Y < 0) { await Load(new(false)); return; }
            if (needs.Newer && e.OffsetDelta.Y > 0) { await Load(new(true)); return; }
        }
        if (e.ExtentDelta != default) return;
        var older = e.OffsetDelta.Y < 0 && scroll.Offset.Y < 64 && list.ItemsPanelRoot is not TranscriptPanel { IsFollowingEnd: true };
        var newer = history() && e.OffsetDelta.Y > 0 && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 64;
        if (!older && !newer) return;
        await Load(new(newer));
    }
    private async Task Load(HistoryRequest request)
    {
        if (paging) return;
        paging = true;
        try
        {
            await browse(request);
            // The swapped page is laid out after this returns; its anchor restore moves the offset
            // and must not read as the user scrolling on to the next page.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception error) { AppDiagnostics.Record("History navigation", error); }
        finally { paging = false; Update(); }
    }
    // Whether the message being read is among `messages`, so switching to them keeps the place.
    public static bool ShowsAnchorFrom(ListBox list, IEnumerable<Message> messages) =>
        list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.CaptureAnchor() is { Item: Message anchor } && messages.Any(m => m.Id == anchor.Id);
    public static void ReplacePage(ListBox list, IReadOnlyList<Message> page)
    {
        var panel = list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault();
        var anchor = panel?.CaptureAnchor();
        list.ItemsSource = page;
        list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.RestoreAnchor(anchor);
    }
}
