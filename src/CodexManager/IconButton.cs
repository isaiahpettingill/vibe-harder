using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using IconPacks.Avalonia.Codicons;
using IconPacks.Avalonia.Core;
using Avalonia.Media;

namespace CodexManager;

public static class AppIcons
{
    public static Control Label(string icon, string text, bool trailing = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var image = Create(icon, trailing ? 8 : 13);
        if (!trailing) row.Children.Add(image);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (trailing) row.Children.Add(image);
        return row;
    }
    // Use the pack's artwork with Avalonia 12's native renderer. The pack's own
    // controls still reference an Avalonia 11 API removed in version 12.
    private static readonly Dictionary<PackIconCodiconsKind, StreamGeometry> GeometryCache = [];
    private static StreamGeometry Geometry(PackIconCodiconsKind kind)
    {
        lock (GeometryCache)
        {
            if (!GeometryCache.TryGetValue(kind, out var geometry))
                GeometryCache[kind] = geometry = StreamGeometry.Parse(PackIconDataFactory<PackIconCodiconsKind>.DataIndex.Value[kind]);
            return geometry;
        }
    }
    public static PathIcon Create(string name, double size = 13) => new()
    {
        Data = Geometry(name switch
        {
            "command" => PackIconCodiconsKind.ListSelection,
            "settings" => PackIconCodiconsKind.Gear,
            "menu" => PackIconCodiconsKind.Menu,
            "more" => PackIconCodiconsKind.Ellipsis,
            "refresh" => PackIconCodiconsKind.Refresh,
            "queue" => PackIconCodiconsKind.ListOrdered,
            "folder" => PackIconCodiconsKind.Folder,
            "latest" => PackIconCodiconsKind.ArrowDown,
            "chevron-up" => PackIconCodiconsKind.ChevronUp,
            "chevron-down" => PackIconCodiconsKind.ChevronDown,
            "chevron-right" => PackIconCodiconsKind.ChevronRight,
            "chevron-left" => PackIconCodiconsKind.ChevronLeft,
            "terminal" => PackIconCodiconsKind.Terminal,
            "keyboard" => PackIconCodiconsKind.KeyboardTab,
            "collapse" => PackIconCodiconsKind.FoldUp,
            "send" => PackIconCodiconsKind.ArrowUp,
            "stop" => PackIconCodiconsKind.DebugStop,
            "add" => PackIconCodiconsKind.Add,
            "remove" => PackIconCodiconsKind.Close,
            "edit" => PackIconCodiconsKind.Edit,
            "archive" => PackIconCodiconsKind.Archive,
            "delete" => PackIconCodiconsKind.Trash,
            "steer" => PackIconCodiconsKind.ArrowRight,
            "model" => PackIconCodiconsKind.SymbolClass,
            "reasoning" => PackIconCodiconsKind.Lightbulb,
            "speed" => PackIconCodiconsKind.SymbolEvent,
            "permission" => PackIconCodiconsKind.Shield,
            "warning" => PackIconCodiconsKind.Warning,
            _ => PackIconCodiconsKind.Copy
        }),
        Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
}

public sealed class IconButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    private string? icon;
    private string? label;
    private double iconSize = 13;
    public double IconSize
    {
        get => iconSize;
        set { iconSize = value; if (Content is PathIcon image) image.Width = image.Height = value; }
    }
    public string Icon
    {
        set
        {
            if (icon == value) return;
            icon = value;
            Content = value == "loading" ? new LoadingSpinner() : AppIcons.Create(value, IconSize);
        }
    }
    public string Label { set { if (label == value) return; label = value; ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); } }
    public IconButton() { Padding = new Thickness(4); MinHeight = OperatingSystem.IsAndroid() ? 44 : 28; MinWidth = OperatingSystem.IsAndroid() ? 44 : 28; Icon = "copy"; }
}
