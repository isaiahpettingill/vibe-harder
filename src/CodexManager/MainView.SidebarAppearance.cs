using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private bool sidebarDragging;
    private bool sidebarHolding;
    private Control WorkspaceDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(8, 7, 8, 7), Opacity = .5, IsHitTestVisible = false };
        line.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBorder"));
        return line;
    }
    private (string Scope, string Id)? sidebarDrag;
    private static readonly DataFormat<string> SidebarDragFormat = DataFormat.CreateStringApplicationFormat("vibeharder-sidebar");
    private sealed record SidebarDrop(string Scope, string Id);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, SidebarDrop> SidebarDropTargets = new();
    private IconButton DragHandle(Control target, string scope, string id, Action refresh, Control? gestureTarget = null)
    {
        var handle = new IconButton { Icon = "drag", IconSize = 10, Label = "Drag to reorder", MinWidth = 16, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Top, Classes = { "rowAction", "dragHandle" } };
        async Task StartDrag(PointerPressedEventArgs e)
        {
            if (sidebarDragging) return;
            e.Handled = true; sidebarDragging = true; sidebarDrag = (scope, id);
            try
            {
                using var data = new DataTransfer(); data.Add(DataTransferItem.Create(SidebarDragFormat, id));
                await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            }
            catch (Exception error) { AppDiagnostics.Record("Reorder sidebar", error); }
            finally { sidebarDragging = false; sidebarDrag = null; target.Opacity = 1; refresh(); }
        }
        handle.AddHandler(PointerPressedEvent, async (_, e) => { if (e.Pointer.Type != PointerType.Touch && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) await StartDrag(e); }, RoutingStrategies.Tunnel);
        var surface = gestureTarget ?? target;
        SidebarDropTargets.AddOrUpdate(target, new(scope, id));
        IDisposable? hold = null;
        IPointer? touchDrag = null;
        Point origin = default;
        void CancelHold() { if (hold is null) return; hold.Dispose(); hold = null; sidebarHolding = false; }
        void FinishTouch(bool rebuild = true)
        {
            if (touchDrag is not { } pointer) return;
            touchDrag = null; sidebarDragging = false; sidebarDrag = null; target.Opacity = 1;
            pointer.Capture(null); if (rebuild && !closing) refresh();
        }
        surface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            CancelHold();
            if (e.Pointer.Type != PointerType.Touch || sidebarDragging || sidebarHolding) return;
            for (var source = e.Source as Visual; source is not null && source != surface; source = Avalonia.VisualTree.VisualExtensions.GetVisualParent(source))
                if (source is IconButton) return;
            origin = e.GetPosition(surface);
            sidebarHolding = true;
            hold = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                CancelHold();
                if (sidebarDragging) return;
                sidebarDragging = true; sidebarDrag = (scope, id); touchDrag = e.Pointer;
                e.Pointer.Capture(surface); target.Opacity = .65;
            }, TimeSpan.FromMilliseconds(500));
        }, RoutingStrategies.Tunnel, true);
        surface.AddHandler(PointerMovedEvent, (_, e) => { if (touchDrag is not null) { e.Handled = true; return; } var delta = e.GetPosition(surface) - origin; if (delta.X * delta.X + delta.Y * delta.Y > 100) CancelHold(); }, RoutingStrategies.Tunnel, true);
        surface.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            CancelHold();
            if (touchDrag != e.Pointer) return;
            e.Handled = true;
            try
            {
                for (var hit = this.InputHitTest(e.GetPosition(this)) as Visual; hit is not null; hit = Avalonia.VisualTree.VisualExtensions.GetVisualParent(hit))
                    if (hit is Control row && SidebarDropTargets.TryGetValue(row, out var drop) && drop.Scope == scope && drop.Id != id)
                    { SidebarOrder.Move(store, scope, id, drop.Id, e.GetPosition(row).Y >= row.Bounds.Height / 2); break; }
            }
            catch (Exception error) { AppDiagnostics.Record("Reorder sidebar", error); }
            finally { FinishTouch(); }
        }, RoutingStrategies.Tunnel, true);
        surface.PointerCaptureLost += (_, _) => { CancelHold(); if (touchDrag?.Captured != surface) FinishTouch(); };
        surface.DetachedFromVisualTree += (_, _) => { CancelHold(); FinishTouch(false); };
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (sidebarDrag?.Scope != scope) { e.DragEffects = DragDropEffects.None; return; }
            var accepts = sidebarDrag is { } drag && drag.Scope == scope && drag.Id != id;
            e.DragEffects = accepts ? DragDropEffects.Move : DragDropEffects.None;
            target.Opacity = accepts ? .65 : 1; e.Handled = true;
        });
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => target.Opacity = 1);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            target.Opacity = 1;
            if (sidebarDrag is not { } drag || drag.Scope != scope || drag.Id == id) return;
            SidebarOrder.Move(store, scope, drag.Id, id, e.GetPosition(target).Y >= target.Bounds.Height / 2);
            e.DragEffects = DragDropEffects.Move; e.Handled = true;
        });
        return handle;
    }
    private void ColorMenu(Control anchor, string key, string label, Action refresh)
    {
        var item = new MenuItem { Header = label + "…" };
        item.Click += (_, _) => SidebarColors.Show(anchor, store, key, label, refresh);
        anchor.ContextMenu ??= new ContextMenu(); anchor.ContextMenu.Items.Add(item);
    }
    private void ApplyChatColors()
    {
        ChatPane.Background = SidebarColors.Brush(store, "workspaceColor:" + workspace?.Id, true);
        ComposerBorder.BorderBrush = SidebarColors.Brush(store, "chatColor:" + current?.Id) ?? this.FindResource("AppBorder") as IBrush;
        ComposerBorder.BorderThickness = new Thickness(SidebarColors.Brush(store, "chatColor:" + current?.Id) is null ? 1 : 2);
    }
}

public static class SidebarColors
{
    private static readonly Dictionary<(Color Color, bool Background), IBrush> brushes = [];
    public static IBrush? Brush(Store store, string key, bool background = false)
    {
        if (!Color.TryParse(store.Setting(key), out var color)) return null;
        lock (brushes)
        {
            if (brushes.TryGetValue((color, background), out var brush)) return brush;
            if (brushes.Count >= 256) brushes.Clear();
            return brushes[(color, background)] = new SolidColorBrush(color, background ? .18 : 1).ToImmutable();
        }
    }
    public static void Show(Control anchor, Store store, string key, string label, Action refresh)
    {
        var popup = new Flyout();
        var input = new TextBox { Text = store.Setting(key) ?? "", PlaceholderText = "#RRGGBB" };
        var error = new TextBlock { Foreground = Brushes.IndianRed };
        void Save(string value) { store.Setting(key, value); popup.Hide(); refresh(); }
        var swatches = new WrapPanel();
        foreach (var value in new[] { "#CBA6F7", "#89B4FA", "#94E2D5", "#A6E3A1", "#F9E2AF", "#FAB387", "#F38BA8" })
        {
            var button = new Button { Background = Avalonia.Media.Brush.Parse(value), Width = 28, Height = 28, Margin = new Thickness(2) };
            ToolTip.SetTip(button, value); button.Click += (_, _) => Save(value); swatches.Children.Add(button);
        }
        var apply = new Button { Content = "Apply", Classes = { "accent" } };
        apply.Click += (_, _) => { if (Color.TryParse(input.Text, out var color)) Save(color.ToString()); else error.Text = "Enter a color such as #89B4FA."; };
        var reset = new Button { Content = "Use theme color" }; reset.Click += (_, _) => Save("");
        popup.Content = new StackPanel { Width = 250, Spacing = 8, Children = { new TextBlock { Text = label }, swatches, input, error, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, reset } } } };
        popup.ShowAt(anchor);
    }
}
