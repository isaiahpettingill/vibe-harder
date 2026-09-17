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
    public static readonly AttachedProperty<bool> ShowProgressProperty = AvaloniaProperty.RegisterAttached<TranscriptPanel, ItemsControl, bool>("ShowProgress");
    public static bool GetShowProgress(ItemsControl control) => control.GetValue(ShowProgressProperty);
    public static void SetShowProgress(ItemsControl control, bool value) => control.SetValue(ShowProgressProperty, value);
    private readonly ChatProgressIndicator progress = new() { Name = "TranscriptProgress", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Margin = new(20, 0, 0, 0) };
    static TranscriptPanel() => ShowProgressProperty.Changed.AddClassHandler<ItemsControl>((owner, _) => owner.ItemsPanelRoot?.InvalidateMeasure());
    private readonly Dictionary<int, Control> realized = [];
    private readonly Dictionary<object, double> heights = new(ReferenceEqualityComparer.Instance);
    private object?[] previousItems = [];
    private double[] positions = [];
    private bool positionsDirty = true;
    private int[] groupStarts = [];
    private readonly Dictionary<int, Message[]> groups = [];
    private int GroupStart(int index) => index >= 0 && index < groupStarts.Length ? groupStarts[index] : index;
    private Vector offset;
    private (int Index, double Within)? pendingAnchor;
    private bool bottom;
    public bool IsFollowingEnd => bottom;
    public void FollowEnd() { pendingAnchor = null; bottom = true; InvalidateMeasure(); }
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
            pendingAnchor = null;
            bottom = next.Y >= Math.Max(0, Extent.Height - Viewport.Height) - 1;
            offset = next; InvalidateMeasure(); RaiseScrollInvalidated(EventArgs.Empty);
        }
    }
    public event EventHandler? ScrollInvalidated;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
    private double HeightAt(int index) => GroupStart(index) != index ? 0 : Items[index] is { } item && heights.TryGetValue(item, out var height) ? height : groups.ContainsKey(index) ? 36 : 100;
    private void EnsurePositions()
    {
        if (!positionsDirty && positions.Length == Items.Count + 1) return;
        if (positions.Length != Items.Count + 1) positions = new double[Items.Count + 1];
        positions[0] = 0;
        for (var i = 0; i < Items.Count; i++) positions[i + 1] = positions[i] + HeightAt(i);
        positionsDirty = false;
    }
    private double Top(int index) { EnsurePositions(); return positions[Math.Clamp(index, 0, Items.Count)]; }
    private int At(double y)
    {
        EnsurePositions();
        var low = 0; var high = Items.Count;
        while (low < high) { var mid = low + (high - low) / 2; if (positions[mid + 1] <= y) low = mid + 1; else high = mid; }
        return GroupStart(Math.Min(low, Math.Max(0, Items.Count - 1)));
    }
    private void SetHeight(int index, double height)
    {
        if (Items[index] is not { } item || heights.TryGetValue(item, out var old) && old == height) return;
        heights[item] = height; positionsDirty = true;
    }
    public void RevealMessage(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index] is not Message message) return;
        message.OutputExpanded = true;
        var start = GroupStart(index);
        if (groups.TryGetValue(start, out var group)) group[0].ActionGroupExpanded = true;
        if (realized.ContainsKey(start)) Release(start);
        heights.Remove(Items[start]!);
        positionsDirty = true;
        ScrollIntoView(index); UpdateLayout();
        if (!realized.TryGetValue(start, out var container)) return;
        var target = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(container).OfType<MessageView>().FirstOrDefault(v => v.Message?.Id == message.Id);
        if (target is not null)
        {
            target.Expand(); UpdateLayout();
            if (target.TranslatePoint(default, this) is { } position) Offset = new Vector(0, offset.Y + position.Y);
            target.Focus();
        }
    }
    public (object Item, double Within)? CaptureAnchor() => Items.Count == 0 ? null : pendingAnchor is { } pending
        ? (Items[pending.Index]!, pending.Within) : (Items[At(offset.Y)]!, offset.Y - Top(At(offset.Y)));
    public void RestoreAnchor((object Item, double Within)? anchor)
    {
        if (anchor is not { } saved) return;
        for (var i = 0; i < Items.Count; i++)
                if (ReferenceEquals(Items[i], saved.Item) || Items[i] is Message item && saved.Item is Message previous && item.Id == previous.Id) { bottom = false; pendingAnchor = (i, saved.Within); offset = new Vector(0, Top(i) + saved.Within); InvalidateMeasure(); break; }
    }
    private Control Realize(int index)
    {
        if (realized.TryGetValue(index, out var existing)) return existing;
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        generator.NeedsContainer(item, index, out var key);
        var control = generator.CreateContainer(item, index, key);
        generator.PrepareItemContainer(control, item, index);
        if (groups.TryGetValue(index, out var group) && control is ContentControl container)
        { container.ContentTemplate = null; container.Content = new TranscriptActionGroup(group, ItemsControl!.ItemTemplate); }
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
        var oldExtent = Extent; var oldViewport = Viewport; var oldOffset = offset;
        if (groupStarts.Length != Items.Count) RebuildGroups(Items);
        if (!Children.Contains(progress)) AddInternalChild(progress);
        var viewport = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 800,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 600);
        if (Math.Abs(width - viewport.Width) > 0.5)
        {
            if (!bottom && Items.Count > 0) pendingAnchor ??= (At(offset.Y), offset.Y - Top(At(offset.Y)));
            heights.Clear(); positionsDirty = true; width = viewport.Width;
        }
        Viewport = viewport;
        progress.IsVisible = ItemsControl is { } owner && GetShowProgress(owner);
        progress.Measure(new Size(viewport.Width, double.PositiveInfinity));
        var progressHeight = progress.DesiredSize.Height;
        if (Items.Count == 0)
        {
            pendingAnchor = null;
            foreach (var index in realized.Keys.ToArray()) Release(index);
            Extent = new(viewport.Width, Math.Max(viewport.Height, progressHeight)); offset = default; RaiseScrollInvalidated(EventArgs.Empty); return viewport;
        }
        // A restored offset can exceed the anchor row's estimated height. Keep the
        // explicit row until measured instead of interpreting it as the next row.
        var anchor = pendingAnchor?.Index ?? At(offset.Y);
        var within = pendingAnchor?.Within ?? offset.Y - Top(anchor);
        pendingAnchor = null;
        var start = bottom ? Items.Count - 1 : Math.Min(anchor, At(Math.Max(0, offset.Y - viewport.Height * 0.2)));
        var end = start;
        double measured = 0;
        if (bottom)
        {
            end = Items.Count - 1;
            for (var i = end; i >= 0; i--)
            {
                if (GroupStart(i) != i) continue;
                var control = Realize(i); control.Measure(new(viewport.Width, double.PositiveInfinity));
                SetHeight(i, Math.Max(1, control.DesiredSize.Height));
                measured += HeightAt(i); start = i;
                if (measured >= viewport.Height * 1.2) break;
            }
        }
        else
        {
            var needed = offset.Y - Top(start) + viewport.Height * 1.4;
            for (var i = start; i < Items.Count; i++)
            {
                if (GroupStart(i) != i) continue;
                var control = Realize(i); control.Measure(new(viewport.Width, double.PositiveInfinity));
                SetHeight(i, Math.Max(1, control.DesiredSize.Height));
                measured += HeightAt(i); end = i;
                if (measured >= needed) break;
            }
        }
        // A tall last row can fill the entire realization budget on its own.
        // Keep preceding rows warm even then, so reversing direction does not
        // recreate all of the nearby markdown controls at once.
        var bufferedRows = 0;
        for (var i = start - 1; i >= 0 && bufferedRows < 4; i--)
        {
            if (GroupStart(i) != i) continue;
            var control = Realize(i); control.Measure(new(viewport.Width, double.PositiveInfinity));
            SetHeight(i, Math.Max(1, control.DesiredSize.Height));
            bufferedRows++; start = i;
        }
        foreach (var index in realized.Keys.Where(i => i < start || i > end).ToArray()) Release(index);
        Extent = new(viewport.Width, Math.Max(viewport.Height, Top(Items.Count) + progressHeight));
        offset = new(0, bottom ? Extent.Height - viewport.Height : Math.Clamp(Top(anchor) + within, 0, Extent.Height - viewport.Height));
        if (Extent != oldExtent || Viewport != oldViewport || offset != oldOffset) RaiseScrollInvalidated(EventArgs.Empty);
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, control) in realized) control.Arrange(new Rect(0, Top(index) - offset.Y, finalSize.Width, HeightAt(index)));
        progress.Arrange(new Rect(0, Top(Items.Count) - offset.Y, finalSize.Width, progress.DesiredSize.Height));
        return finalSize;
    }
    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        var oldGroups = groups.ToDictionary(p => p.Key, p => p.Value);
        var oldContainers = realized.ToArray();
        (object Item, double Within)? anchor = null;
        if (!bottom && pendingAnchor is { } pending && pending.Index < previousItems.Length)
            anchor = (previousItems[pending.Index]!, pending.Within);
        else if (!bottom && previousItems.Length > 0)
        {
            var within = offset.Y;
            for (var i = 0; i < previousItems.Length; i++)
            {
                if (GroupStart(i) != i) continue;
                var h = previousItems[i] is { } item && heights.TryGetValue(item, out var cached) ? cached : oldGroups.ContainsKey(i) ? 36 : 100;
                if (within < h || i == previousItems.Length - 1) { anchor = (previousItems[i]!, within); break; }
                within -= h;
            }
        }
        pendingAnchor = null;
        RebuildGroups(items);
        var indices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < items.Count; i++) if (items[i] is { } item) indices.TryAdd(item, i);
        realized.Clear();
        foreach (var (oldIndex, control) in oldContainers)
        {
            var oldItem = oldIndex < previousItems.Length ? previousItems[oldIndex] : null;
            if (oldItem is not null && indices.TryGetValue(oldItem, out var index) && GroupStart(index) == index &&
                (oldGroups.GetValueOrDefault(oldIndex) ?? []).SequenceEqual(groups.GetValueOrDefault(index) ?? []))
            {
                realized[index] = control;
                if (index != oldIndex) ItemContainerGenerator!.ItemContainerIndexChanged(control, oldIndex, index);
            }
            else { RemoveInternalChild(control); ItemContainerGenerator!.ClearItemContainer(control); }
        }
        previousItems = items.ToArray();
        var remaining = new HashSet<object>(items.Where(i => i is not null)!, ReferenceEqualityComparer.Instance);
        foreach (var item in heights.Keys.Where(i => !remaining.Contains(i)).ToArray()) heights.Remove(item);
        positionsDirty = true;
        RestoreAnchor(anchor);
        InvalidateMeasure();
    }
    private void RebuildGroups(IReadOnlyList<object?> items)
    {
        var previous = groups.Values.ToDictionary(group => group[0], group => group);
        groups.Clear(); groupStarts = new int[items.Count];
        positionsDirty = true;
        for (var i = 0; i < items.Count;)
        {
            var start = i++;
            if (TranscriptActionGroup.IsAction(items[start]))
                while (i < items.Count && TranscriptActionGroup.IsAction(items[i])) i++;
            for (var member = start; member < i; member++) groupStarts[member] = start;
            if (i - start > 1)
            {
                groups[start] = items.Skip(start).Take(i - start).Cast<Message>().ToArray();
                if (!previous.TryGetValue(groups[start][0], out var old) || !old.SequenceEqual(groups[start])) heights.Remove(items[start]!);
            }
        }
        if (previousItems.Length == 0) previousItems = items.ToArray();
    }
    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        pendingAnchor = null;
        bottom = index == Items.Count - 1;
        index = GroupStart(index);
        offset = new(0, Top(index)); InvalidateMeasure();
        return realized.GetValueOrDefault(index);
    }
    protected override Control? ContainerFromIndex(int index) => realized.GetValueOrDefault(GroupStart(index));
    protected override int IndexFromContainer(Control container) => realized.FirstOrDefault(pair => pair.Value == container, new(-1, container)).Key;
    protected override IEnumerable<Control>? GetRealizedContainers() => realized.Values;
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) =>
        from is Control control ? GetControlInDirection(direction, control) : null;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from)
    {
        var index = from is null ? 0 : IndexFromContainer(from);
        return ScrollIntoView(Math.Clamp(index + (direction == NavigationDirection.Up ? -1 : groups.TryGetValue(index, out var group) ? group.Length : 1), 0, Math.Max(0, Items.Count - 1)));
    }
    public bool BringIntoView(Control target, Rect targetRect)
    {
        var index = IndexFromContainer(target);
        if (index < 0) return false;
        var top = Top(index) + targetRect.Top;
        var end = top + targetRect.Height;
        // Focus/selection can request an already-visible row after layout. Moving
        // it to the top would disturb reading and disable the bottom anchor.
        if (top >= offset.Y && end <= offset.Y + Viewport.Height || top <= offset.Y && end >= offset.Y + Viewport.Height) return false;
        Offset = new Vector(0, top < offset.Y ? top : end - Viewport.Height);
        return true;
    }
}
