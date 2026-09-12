using Avalonia.Controls;
using Avalonia.Interactivity;
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
        if (paging || list.Scroll is not { } scroll || e.OffsetDelta.Y == 0 || e.ExtentDelta.Y != 0) return;
        var older = e.OffsetDelta.Y < 0 && scroll.Offset.Y < 64;
        var newer = history() && e.OffsetDelta.Y > 0 && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 64;
        if (!older && !newer) return;
        paging = true;
        try { await browse(newer); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("History navigation: " + error); }
        finally { paging = false; Update(); }
    }
    public static void ReplacePage(ListBox list, IReadOnlyList<Message> page)
    {
        var panel = list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault();
        var anchor = panel?.CaptureAnchor();
        list.ItemsSource = page; list.UpdateLayout();
        list.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.RestoreAnchor(anchor);
    }
}
