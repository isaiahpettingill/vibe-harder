using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace CodexManager;

// Pixel scrolling with an explicit bottom anchor. Unlike an average-height
// virtual stack, measuring a tall markdown row cannot move the bottom away.
public sealed class TranscriptPanel : VirtualizingPanel, ILogicalScrollable
{
    private readonly Dictionary<int, Control> realized = [];
    private readonly Dictionary<object, double> heights = new(ReferenceEqualityComparer.Instance);
    private Vector offset;
    private bool bottom;
    private double width;
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(0, 32);
    public Size PageScrollSize => new(0, Math.Max(32, Viewport.Height - 32));
    public Size Extent { get; private set; }
    public Size Viewport { get; private set; }
    public Vector Offset
    {
        get => offset;
        set
        {
            var next = new Vector(0, Math.Clamp(value.Y, 0, Math.Max(0, Extent.Height - Viewport.Height)));
            if (next == offset) return;
            bottom = next.Y >= Math.Max(0, Extent.Height - Viewport.Height) - 1;
            offset = next; InvalidateMeasure(); RaiseScrollInvalidated(EventArgs.Empty);
        }
    }
    public event EventHandler? ScrollInvalidated;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
    private double HeightAt(int index) => Items[index] is { } item && heights.TryGetValue(item, out var height) ? height : 100;
    private double Top(int index) { double result = 0; for (var i = 0; i < index; i++) result += HeightAt(i); return result; }
    private int At(double y) { var i = 0; while (i < Items.Count - 1 && y >= HeightAt(i)) y -= HeightAt(i++); return i; }
    public (object Item, double Within)? CaptureAnchor() => Items.Count == 0 ? null : (Items[At(offset.Y)]!, offset.Y - Top(At(offset.Y)));
    public void RestoreAnchor((object Item, double Within)? anchor)
    {
        if (anchor is not { } saved) return;
        for (var i = 0; i < Items.Count; i++)
            if (ReferenceEquals(Items[i], saved.Item) || Items[i] is Message item && saved.Item is Message previous && item.Id == previous.Id) { bottom = false; offset = new Vector(0, Top(i) + saved.Within); InvalidateMeasure(); RaiseScrollInvalidated(EventArgs.Empty); break; }
    }
    private Control Realize(int index)
    {
        if (realized.TryGetValue(index, out var existing)) return existing;
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        generator.NeedsContainer(item, index, out var key);
        var control = generator.CreateContainer(item, index, key);
        generator.PrepareItemContainer(control, item, index);
        realized[index] = control; AddInternalChild(control);
        generator.ItemContainerPrepared(control, item, index);
        return control;
    }
    private void Release(int index)
    {
        var control = realized[index]; realized.Remove(index);
        RemoveInternalChild(control); ItemContainerGenerator!.ClearItemContainer(control);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var viewport = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 800,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 600);
        if (Math.Abs(width - viewport.Width) > 0.5) { heights.Clear(); width = viewport.Width; }
        Viewport = viewport;
        if (Items.Count == 0)
        {
            foreach (var index in realized.Keys.ToArray()) Release(index);
            Extent = viewport; offset = default; RaiseScrollInvalidated(EventArgs.Empty); return viewport;
        }
        var anchor = At(offset.Y);
        var within = offset.Y - Top(anchor);
        var start = bottom ? Items.Count - 1 : At(Math.Max(0, offset.Y - viewport.Height * 0.2));
        var end = start;
        double measured = 0;
        if (bottom)
        {
            end = Items.Count - 1;
            for (var i = end; i >= 0; i--)
            {
                var control = Realize(i); control.Measure(new(viewport.Width, double.PositiveInfinity));
                heights[Items[i]!] = Math.Max(1, control.DesiredSize.Height);
                measured += HeightAt(i); start = i;
                if (measured >= viewport.Height * 1.2) break;
            }
        }
        else
        {
            for (var i = start; i < Items.Count; i++)
            {
                var control = Realize(i); control.Measure(new(viewport.Width, double.PositiveInfinity));
                heights[Items[i]!] = Math.Max(1, control.DesiredSize.Height);
                measured += HeightAt(i); end = i;
                if (measured >= viewport.Height * 1.4) break;
            }
        }
        foreach (var index in realized.Keys.Where(i => i < start || i > end).ToArray()) Release(index);
        Extent = new(viewport.Width, Math.Max(viewport.Height, Top(Items.Count)));
        offset = new(0, bottom ? Extent.Height - viewport.Height : Math.Clamp(Top(anchor) + within, 0, Extent.Height - viewport.Height));
        RaiseScrollInvalidated(EventArgs.Empty);
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, control) in realized) control.Arrange(new Rect(0, Top(index) - offset.Y, finalSize.Width, HeightAt(index)));
        return finalSize;
    }
    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        foreach (var index in realized.Keys.ToArray()) Release(index);
        var remaining = new HashSet<object>(items.Where(i => i is not null)!, ReferenceEqualityComparer.Instance);
        foreach (var item in heights.Keys.Where(i => !remaining.Contains(i)).ToArray()) heights.Remove(item);
        InvalidateMeasure();
    }
    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        bottom = index == Items.Count - 1;
        offset = new(0, Top(index)); InvalidateMeasure();
        return realized.GetValueOrDefault(index);
    }
    protected override Control? ContainerFromIndex(int index) => realized.GetValueOrDefault(index);
    protected override int IndexFromContainer(Control container) => realized.FirstOrDefault(pair => pair.Value == container, new(-1, container)).Key;
    protected override IEnumerable<Control>? GetRealizedContainers() => realized.Values;
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) =>
        from is Control control ? GetControlInDirection(direction, control) : null;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from)
    {
        var index = from is null ? 0 : IndexFromContainer(from);
        return ScrollIntoView(Math.Clamp(index + (direction == NavigationDirection.Up ? -1 : 1), 0, Math.Max(0, Items.Count - 1)));
    }
    public bool BringIntoView(Control target, Rect targetRect)
    {
        var index = IndexFromContainer(target);
        if (index < 0) return false;
        ScrollIntoView(index); return true;
    }
}
