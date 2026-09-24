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
    private IPointer? sidebarTouchPointer;
    private Point sidebarTouchOrigin;
    private bool sidebarTouchScrolled;
    private void TrackSidebarTouch()
    {
        WorkspaceScroll.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch) return;
            sidebarTouchPointer = e.Pointer;
            sidebarTouchOrigin = e.GetPosition(WorkspaceScroll);
            sidebarTouchScrolled = false;
        }, RoutingStrategies.Tunnel, true);
        WorkspaceScroll.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (e.Pointer != sidebarTouchPointer) return;
            var delta = e.GetPosition(WorkspaceScroll) - sidebarTouchOrigin;
            if (delta.X * delta.X + delta.Y * delta.Y > 100) sidebarTouchScrolled = true;
        }, RoutingStrategies.Tunnel, true);
        WorkspaceScroll.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.Pointer != sidebarTouchPointer) return;
            var released = e.Pointer;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (sidebarTouchPointer == released) { sidebarTouchPointer = null; sidebarTouchScrolled = false; }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel, true);
    }
    private bool IsSidebarTouchSwipe(IPointer pointer) => pointer.Type == PointerType.Touch && pointer == sidebarTouchPointer && sidebarTouchScrolled;
    private bool SidebarTouchSwipe => sidebarTouchPointer is not null && sidebarTouchScrolled;
    private Control WorkspaceDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(8, 7, 8, 7), IsHitTestVisible = false };
        line.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBorder"));
        return line;
    }
    private sealed record SidebarDrop(string Scope, string Id, Control Anchor);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, SidebarDrop> SidebarDropTargets = new();
    private bool sidebarRebuildPending;
    private readonly Border sidebarDropIndicator = new() { Height = 2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ZIndex = 1000 };
    private void EnableHoldReorder(Control target, string scope, string id, Action refresh, Control? gestureTarget = null, Action<string, string, bool>? move = null)
    {
        var surface = gestureTarget ?? target;
        SidebarDropTargets.AddOrUpdate(target, new(scope, id, surface));
        IDisposable? hold = null;
        IPointer? pointer = null;
        InputElement? inputRoot = null;
        Point origin = default;
        bool dragging = false;
        (Control Row, SidebarDrop Drop, bool After)? destination = null;
        void Finish(bool rebuild = true)
        {
            hold?.Dispose(); hold = null;
            var previous = pointer; pointer = null;
            var wasDragging = dragging; dragging = false;
            if (inputRoot is { } root)
            {
                root.RemoveHandler(PointerMovedEvent, Moved); root.RemoveHandler(PointerReleasedEvent, Released); root.RemoveHandler(KeyDownEvent, KeyPressed);
                inputRoot = null;
            }
            sidebarHolding = false; sidebarDragging = false; destination = null;
            target.Classes.Remove("dragging"); RootPanes.Children.Remove(sidebarDropIndicator);
            if (previous?.Captured == surface) previous.Capture(null);
            if (rebuild && !closing)
            {
                if (sidebarRebuildPending) { sidebarRebuildPending = false; BuildWorkspaceTree(); }
                else if (wasDragging) refresh();
            }
        }
        void Start()
        {
            if (pointer is null || dragging || closing) return;
            hold?.Dispose(); hold = null; sidebarHolding = false;
            dragging = true; sidebarDragging = true;
            pointer.Capture(surface); target.Classes.Add("dragging");
        }
        void FindDestination(Point position)
        {
            destination = null; RootPanes.Children.Remove(sidebarDropIndicator);
            for (var hit = this.InputHitTest(position) as Visual; hit is not null; hit = Avalonia.VisualTree.VisualExtensions.GetVisualParent(hit))
            {
                if (hit is not Control row) continue;
                // The ListBoxItem's padding also belongs to its chat's drop area.
                var candidate = row;
                if (row is ListBoxItem)
                    candidate = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(row).OfType<Control>().FirstOrDefault(c => SidebarDropTargets.TryGetValue(c, out var d) && d.Scope == scope) ?? row;
                if (!SidebarDropTargets.TryGetValue(candidate, out var drop) || drop.Scope != scope) continue;
                if (drop.Id == id) return;
                var relative = this.TranslatePoint(position, drop.Anchor);
                if (relative is null) return;
                var after = relative.Value.Y >= drop.Anchor.Bounds.Height / 2;
                destination = (candidate, drop, after);
                var edge = after ? candidate.Bounds.Height : 0;
                if (candidate.TranslatePoint(new Point(0, edge), RootPanes) is { } point)
                {
                    sidebarDropIndicator.Background = this.FindResource("AppAccent") as IBrush;
                    sidebarDropIndicator.Width = candidate.Bounds.Width;
                    sidebarDropIndicator.Margin = new Thickness(point.X, point.Y - 1, 0, 0);
                    Grid.SetColumnSpan(sidebarDropIndicator, 3); Grid.SetRowSpan(sidebarDropIndicator, 3);
                    RootPanes.Children.Add(sidebarDropIndicator);
                }
                return;
            }
        }
        void Moved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer != pointer) return;
            if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { Finish(); return; }
            var position = e.GetPosition(this);
            if (!dragging)
            {
                var delta = position - origin;
                if (delta.X * delta.X + delta.Y * delta.Y <= 64) return;
                if (e.Pointer.Type == PointerType.Touch) { Finish(); return; }
                Start();
            }
            if (dragging) { e.Handled = true; FindDestination(position); }
        }
        void Released(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer != pointer) return;
            try
            {
                if (!dragging) return;
                e.Handled = true; FindDestination(e.GetPosition(this));
                if (destination is { } drop)
                {
                    if (move is { } remoteMove) remoteMove(id, drop.Drop.Id, drop.After);
                    else SidebarOrder.Move(store, scope, id, drop.Drop.Id, drop.After);
                }
            }
            catch (Exception error) { AppDiagnostics.Record("Reorder sidebar", error); }
            finally { Finish(); }
        }
        void KeyPressed(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && pointer is not null) { e.Handled = true; Finish(); }
        }
        surface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Touch || sidebarDragging || sidebarHolding || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            for (var source = e.Source as Visual; source is not null && source != surface; source = Avalonia.VisualTree.VisualExtensions.GetVisualParent(source))
                if (source is IconButton or CheckBox or TextBox) return;
            origin = e.GetPosition(this); pointer = e.Pointer; sidebarHolding = true;
            // ListBoxItem and Button capture presses. Observe their subsequent events
            // at the root so release/movement is not lost outside the inner row.
            inputRoot = TopLevel.GetTopLevel(surface) ?? (InputElement)this;
            inputRoot.AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
            inputRoot.AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel, true);
            inputRoot.AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel, true);
            hold = Avalonia.Threading.DispatcherTimer.RunOnce(Start, TimeSpan.FromMilliseconds(500));
        }, RoutingStrategies.Tunnel, true);
        surface.PointerCaptureLost += (_, _) => { if (dragging && pointer?.Captured != surface) Finish(); };
        surface.DetachedFromVisualTree += (_, _) => { if (pointer is not null) Finish(false); };
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
    private static readonly Dictionary<(Color Color, bool Background), WeakReference<SolidColorBrush>> brushes = [];
    private static Color Opaque(Color color, bool background)
    {
        var canvas = Color.Parse(AppTheme.Current?.Background ?? "#14141C");
        var alpha = color.A / 255d * (background ? .18 : 1);
        byte Blend(byte value, byte basis) => (byte)Math.Round(value * alpha + basis * (1 - alpha));
        return Color.FromRgb(Blend(color.R, canvas.R), Blend(color.G, canvas.G), Blend(color.B, canvas.B));
    }
    public static void RefreshTheme()
    {
        lock (brushes)
            foreach (var (key, weak) in brushes.ToArray())
                if (weak.TryGetTarget(out var brush)) brush.Color = Opaque(key.Color, key.Background);
                else brushes.Remove(key);
    }
    public static IBrush? Brush(Store store, string key, bool background = false)
    {
        if (!Color.TryParse(store.Setting(key), out var color)) return null;
        lock (brushes)
        {
            if (brushes.TryGetValue((color, background), out var weak) && weak.TryGetTarget(out var cached)) return cached;
            if (brushes.Count % 64 == 0) RefreshTheme();
            var brush = new SolidColorBrush(Opaque(color, background));
            brushes[(color, background)] = new(brush);
            return brush;
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
