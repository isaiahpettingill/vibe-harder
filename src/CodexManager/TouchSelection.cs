using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ColorDocument.Avalonia;
using ColorTextBlock.Avalonia;

namespace CodexManager;

// Chat text selects the way text does in other phone apps: hold a word to select it, keep the
// finger down or drag the handles to change the selection, and copy from the bar above it.
// Dragging anywhere else still scrolls the transcript.
public sealed class TouchSelection
{
    private static TouchSelection? active;
    // The renderer keeps a caret's place on its line internal; selection highlighting uses the same values.
    private static readonly PropertyInfo? GeometryProperty = typeof(ColorTextBlock.Avalonia.TextPointer).GetProperty("Geometry", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? DistanceProperty = typeof(ColorTextBlock.Avalonia.TextPointer).GetProperty("Distance", BindingFlags.Instance | BindingFlags.NonPublic);
    private const double HandleSize = 22, HandlePadding = 10;
    private readonly ChatMarkdown owner;
    private readonly Canvas layer = new() { Name = "TouchSelectionLayer", IsVisible = false };
    private readonly Border startHandle, endHandle, bar;
    // Both ends as caret rectangles in the document's coordinates.
    private Rect start, end;
    private IPointer? extending;
    private TopLevel? top;

    internal TouchSelection(ChatMarkdown owner)
    {
        this.owner = owner;
        startHandle = Handle("SelectionStartHandle", new CornerRadius(HandleSize / 2, 0, HandleSize / 2, HandleSize / 2), first: true);
        endHandle = Handle("SelectionEndHandle", new CornerRadius(0, HandleSize / 2, HandleSize / 2, HandleSize / 2), first: false);
        var copy = new Button { Name = "CopySelection", Content = "Copy" };
        var all = new Button { Name = "SelectAllText", Content = "Select all" };
        copy.Click += async (_, _) => { await owner.Copy(true); Clear(); };
        all.Click += (_, _) => SelectAll();
        bar = new Border { Name = "SelectionBar", CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), IsVisible = false, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { copy, all } } };
        bar.Bind(Border.BackgroundProperty, owner.GetResourceObservable("AppSurface"));
        bar.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("AppBorder"));
        layer.Children.Add(startHandle); layer.Children.Add(endHandle); layer.Children.Add(bar);
        LongPress.Attach(owner, Hold);
        owner.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer, extending) || owner.SelectionDocument is not { } document) return;
            e.PreventGestureRecognition(); e.Handled = true;
            if (At(e.GetPosition(document.Control), nearest: true) is { } caret) { end = caret.Caret; Apply(); }
        }, RoutingStrategies.Tunnel, true);
        owner.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer, extending)) return;
            extending = null; e.Pointer.Capture(null); e.Handled = true; ShowBar();
        }, RoutingStrategies.Tunnel, true);
        owner.DetachedFromVisualTree += (_, _) => Clear();
    }
    public bool IsActive => layer.IsVisible;
    public string SelectedText => owner.SelectedText();

    // The word around a character: letters, digits and underscores, or the single other character.
    public static (int Start, int End) Word(string text, int index)
    {
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length - 1);
        static bool Part(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (!Part(text[index]))
        {
            // A finger past the middle of a word's last letter lands just after the word.
            if (index > 0 && Part(text[index - 1])) index--;
            else return (index, index + 1);
        }
        var from = index; while (from > 0 && Part(text[from - 1])) from--;
        var to = index; while (to < text.Length && Part(text[to])) to++;
        return (from, to);
    }

    private void Hold(Point point, IPointer pointer)
    {
        if (owner.HoldsLink || owner.SelectionDocument is not { } document || owner.TranslatePoint(point, document.Control) is not { } at) return;
        if (At(at, nearest: false) is not { } hit) return;
        var text = hit.Block.Text; var (from, to) = Word(text, hit.Index);
        if (to <= from) return;
        if (active != this) active?.Clear();
        active = this;
        var origin = hit.Block.TranslatePoint(default, document.Control) ?? default;
        start = Caret(hit.Block.CalcuatePointerFrom(from), origin);
        end = Caret(to < text.Length ? hit.Block.CalcuatePointerFrom(to) : hit.Block.GetEnd(), origin);
        Show(); Apply();
        // Keep the finger down and move it to grow the selection, as on Android.
        extending = pointer; pointer.Capture(owner);
    }
    private void SelectAll()
    {
        if (owner.SelectionDocument is not { } document) return;
        var blocks = document.Control.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        if (blocks.Length == 0) return;
        start = Caret(blocks[0].GetBegin(), blocks[0].TranslatePoint(default, document.Control) ?? default);
        end = Caret(blocks[^1].GetEnd(), blocks[^1].TranslatePoint(default, document.Control) ?? default);
        Apply(); ShowBar();
    }
    public void Clear()
    {
        if (active == this) active = null;
        if (!layer.IsVisible) return;
        layer.IsVisible = false; bar.IsVisible = false; extending = null;
        owner.SelectionDocument?.UnSelect();
        top?.RemoveHandler(InputElement.PointerPressedEvent, PressedElsewhere); top = null;
    }
    private void Show()
    {
        if (AdornerLayer.GetAdorner(owner) != layer) AdornerLayer.SetAdorner(owner, layer);
        layer.IsVisible = true; bar.IsVisible = false;
        if (top is null && TopLevel.GetTopLevel(owner) is { } level)
        { top = level; top.AddHandler(InputElement.PointerPressedEvent, PressedElsewhere, RoutingStrategies.Tunnel, true); }
    }
    // A tap anywhere but the handles or the bar ends the selection; the tap itself still works.
    private void PressedElsewhere(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source == layer || layer.IsVisualAncestorOf(source))) return;
        Clear();
    }
    private void Apply()
    {
        if (owner.SelectionDocument is not { } document) return;
        document.Select(start.Center, end.Center);
        var (first, last) = start.Top < end.Top || start.Top == end.Top && start.X <= end.X ? (start, end) : (end, start);
        Place(startHandle, document, new Point(first.X - HandleSize - HandlePadding, first.Bottom - HandlePadding));
        Place(endHandle, document, new Point(last.X - HandlePadding, last.Bottom - HandlePadding));
    }
    private void Place(Control control, DocumentElement document, Point at)
    {
        var point = document.Control.TranslatePoint(at, owner) ?? at;
        Canvas.SetLeft(control, point.X); Canvas.SetTop(control, point.Y);
    }
    private void ShowBar()
    {
        if (owner.SelectionDocument is not { } document || !layer.IsVisible) return;
        bar.IsVisible = true; bar.Measure(Size.Infinity);
        var top = Math.Min(start.Top, end.Top); var bottom = Math.Max(start.Bottom, end.Bottom);
        var y = top - bar.DesiredSize.Height - 8;
        if ((document.Control.TranslatePoint(new Point(0, y), owner)?.Y ?? y) < 0) y = bottom + HandleSize + HandlePadding + 4;
        var x = Math.Clamp(Math.Min(start.X, end.X), 0, Math.Max(0, owner.Bounds.Width - bar.DesiredSize.Width));
        Place(bar, document, new Point(x, y));
    }
    private Border Handle(string name, CornerRadius shape, bool first)
    {
        var grip = new Border { Width = HandleSize, Height = HandleSize, CornerRadius = shape };
        grip.Bind(Border.BackgroundProperty, owner.GetResourceObservable("AppAccent"));
        // The touch target is larger than the drawn handle.
        var handle = new Border { Name = name, Background = Brushes.Transparent, Padding = new Thickness(HandlePadding), Child = grip };
        handle.PointerPressed += (_, e) => { e.Pointer.Capture(handle); e.PreventGestureRecognition(); e.Handled = true; bar.IsVisible = false; };
        handle.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, handle) || owner.SelectionDocument is not { } document) return;
            e.PreventGestureRecognition(); e.Handled = true;
            // The handle hangs below its line, its tip at the caret: aim at the text above the finger.
            var finger = e.GetPosition(document.Control);
            var movesStart = first == (start.Top < end.Top || start.Top == end.Top && start.X <= end.X);
            var caret = movesStart ? start : end;
            var target = new Point(finger.X + (first ? HandleSize / 2 : -HandleSize / 2), finger.Y - HandleSize / 2 - caret.Height / 2);
            if (At(target, nearest: true) is not { } hit) return;
            if (movesStart) start = hit.Caret; else end = hit.Caret;
            Apply();
        };
        handle.PointerReleased += (_, e) => { if (ReferenceEquals(e.Pointer.Captured, handle)) { e.Pointer.Capture(null); e.Handled = true; ShowBar(); } };
        return handle;
    }

    // The text block and character under a point in the document, with that character's caret.
    private (CTextBlock Block, int Index, Rect Caret)? At(Point point, bool nearest)
    {
        if (owner.SelectionDocument is not { } document) return null;
        CTextBlock? best = null; Rect bounds = default; var distance = double.MaxValue;
        foreach (var block in document.Control.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (!block.IsEffectivelyVisible || block.TranslatePoint(default, document.Control) is not { } origin) continue;
            var rect = new Rect(origin, block.Bounds.Size);
            var away = point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : point.X < rect.Left ? rect.Left - point.X : point.X > rect.Right ? point.X - rect.Right : 0;
            if (away < distance) { distance = away; best = block; bounds = rect; }
        }
        if (best is null || !nearest && distance > 0) return null;
        var local = new Point(point.X - bounds.X, Math.Clamp(point.Y - bounds.Y, 0, Math.Max(0, bounds.Height - 1)));
        var pointer = best.CalcuatePointerFrom(local.X, local.Y);
        return (best, pointer.Index, Caret(pointer, bounds.Position));
    }
    private static Rect Caret(ColorTextBlock.Avalonia.TextPointer pointer, Point origin) =>
        GeometryProperty?.GetValue(pointer) is ColorTextBlock.Avalonia.Geometries.CGeometry line && DistanceProperty?.GetValue(pointer) is double distance
            ? new Rect(origin.X + line.Left + distance, origin.Y + line.Top, 0, line.Height)
            : new Rect(origin, new Size(0, 16));
}
