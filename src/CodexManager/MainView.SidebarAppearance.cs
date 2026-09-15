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
    private sealed record SidebarDrop(string Scope, string Id);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, SidebarDrop> SidebarDropTargets = new();
    private void EnableHoldReorder(Control target, string scope, string id, Action refresh, Control? gestureTarget = null)
    {
        var surface = gestureTarget ?? target;
        SidebarDropTargets.AddOrUpdate(target, new(scope, id));
        IDisposable? hold = null;
        IPointer? dragPointer = null;
        Point origin = default;
        void CancelHold() { if (hold is null) return; hold.Dispose(); hold = null; sidebarHolding = false; }
        void FinishDrag(bool rebuild = true)
        {
            if (dragPointer is not { } pointer) return;
            dragPointer = null; sidebarDragging = false; target.Opacity = 1;
            pointer.Capture(null); if (rebuild && !closing) refresh();
        }
        surface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            CancelHold();
            if (sidebarDragging || sidebarHolding || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            for (var source = e.Source as Visual; source is not null && source != surface; source = Avalonia.VisualTree.VisualExtensions.GetVisualParent(source))
                if (source is IconButton or CheckBox or TextBox) return;
            origin = e.GetPosition(surface);
            sidebarHolding = true;
            hold = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                CancelHold();
                if (sidebarDragging) return;
                sidebarDragging = true; dragPointer = e.Pointer;
                e.Pointer.Capture(surface); target.Opacity = .65;
            }, TimeSpan.FromMilliseconds(500));
        }, RoutingStrategies.Tunnel, true);
        surface.AddHandler(PointerMovedEvent, (_, e) => { if (dragPointer is not null) { e.Handled = true; return; } var delta = e.GetPosition(surface) - origin; if (delta.X * delta.X + delta.Y * delta.Y > 100) CancelHold(); }, RoutingStrategies.Tunnel, true);
        surface.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            CancelHold();
            if (dragPointer != e.Pointer) return;
            e.Handled = true;
            try
            {
                for (var hit = this.InputHitTest(e.GetPosition(this)) as Visual; hit is not null; hit = Avalonia.VisualTree.VisualExtensions.GetVisualParent(hit))
                    if (hit is Control row && SidebarDropTargets.TryGetValue(row, out var drop) && drop.Scope == scope && drop.Id != id)
                    { SidebarOrder.Move(store, scope, id, drop.Id, e.GetPosition(row).Y >= row.Bounds.Height / 2); break; }
            }
            catch (Exception error) { AppDiagnostics.Record("Reorder sidebar", error); }
            finally { FinishDrag(); }
        }, RoutingStrategies.Tunnel, true);
        surface.PointerCaptureLost += (_, _) => { CancelHold(); if (dragPointer?.Captured != surface) FinishDrag(); };
        surface.DetachedFromVisualTree += (_, _) => { CancelHold(); FinishDrag(false); };
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
