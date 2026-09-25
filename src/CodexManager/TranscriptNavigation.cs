using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CodexManager;

public sealed class TranscriptNavigation
{
    private readonly ListBox list;
    private readonly IconButton latest;
    private readonly Func<bool> history;
    private readonly Func<bool, Task> browse;
    private bool paging;
    public TranscriptNavigation(ListBox list, IconButton latest, Func<bool> history, Func<bool, Task> browse)
    {
        this.list = list; this.latest = latest; this.history = history; this.browse = browse;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, Changed, RoutingStrategies.Bubble);
        list.AddHandler(InputElement.PointerWheelChangedEvent, async (_, e) =>
        {
            if (list.Scroll is not { } scroll) return;
            if (e.Delta.Y > 0 && scroll.Offset.Y < 64) await Load(false);
            else if (e.Delta.Y < 0 && history() && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 64) await Load(true);
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
        if (paging || list.Scroll is not { } scroll || e.OffsetDelta.Y == 0 || e.ExtentDelta != default || e.ViewportDelta != default) return;
        var older = e.OffsetDelta.Y < 0 && scroll.Offset.Y < 64 && list.ItemsPanelRoot is not TranscriptPanel { IsFollowingEnd: true };
        var newer = history() && e.OffsetDelta.Y > 0 && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 64;
        if (!older && !newer) return;
        await Load(newer);
    }
    private async Task Load(bool newer)
    {
        if (paging) return;
        paging = true;
        try { await browse(newer); }
        catch (Exception error) { AppDiagnostics.Record("History navigation", error); }
        finally { paging = false; Update(); }
    }
    public static void ReplacePage(ListBox list, IReadOnlyList<Message> page)
    {
        var panel = list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault();
        var anchor = panel?.CaptureAnchor();
        list.ItemsSource = page;
        list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.RestoreAnchor(anchor);
    }
}
