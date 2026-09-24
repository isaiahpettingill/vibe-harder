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
    private static readonly Dictionary<string, StreamGeometry> AdditionalGeometry = [];
    private static StreamGeometry Additional(string name)
    {
        lock (AdditionalGeometry)
        {
            if (AdditionalGeometry.TryGetValue(name, out var geometry)) return geometry;
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://VibeHarder.UI/Assets/Codicons/{name}.svg"));
            var svg = System.Xml.Linq.XDocument.Load(stream);
            return AdditionalGeometry[name] = StreamGeometry.Parse(string.Join(" ", svg.Descendants().Where(e => e.Name.LocalName == "path").Select(e => (string?)e.Attribute("d"))));
        }
    }
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
        Data = name is "openai" or "claude" or "steer" or "agent" or "mode" or "permission" or "command" ? Additional(name switch { "steer" => "forward", "mode" => "edit-compact", "permission" => "shield-compact", "command" => "layout-menubar", _ => name }) : Geometry(name switch
        {
            "provider" => PackIconCodiconsKind.Server,
            "yolo" => PackIconCodiconsKind.Rocket,
            "auto-approve" => PackIconCodiconsKind.Check,
            "budget" => PackIconCodiconsKind.Dashboard,
            "settings" => PackIconCodiconsKind.Gear,
            "menu" => PackIconCodiconsKind.Menu,
            "more" => PackIconCodiconsKind.Ellipsis,
            "refresh" => PackIconCodiconsKind.Refresh,
            "queue" => PackIconCodiconsKind.ListOrdered,
            "folder" => PackIconCodiconsKind.Folder,
            "new-folder" => PackIconCodiconsKind.NewFolder,
            "search" => PackIconCodiconsKind.Search,
            "fullscreen" => PackIconCodiconsKind.ScreenFull,
            "latest" => PackIconCodiconsKind.ArrowDown,
            "download" => PackIconCodiconsKind.ArrowDown,
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
            "drag" => PackIconCodiconsKind.Gripper,
            "fork" => PackIconCodiconsKind.RepoForked,
            "model" => PackIconCodiconsKind.SymbolClass,
            "reasoning" => PackIconCodiconsKind.Lightbulb,
            "speed" => PackIconCodiconsKind.SymbolEvent,
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
    public Func<bool>? ClickAllowed { get; set; }
    protected override void OnClick()
    {
        if (ClickAllowed?.Invoke() == false) return;
        base.OnClick();
    }
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
            Content = value == "connecting" ? new ConnectingIndicator() : value == "loading" ? new LoadingSpinner() : AppIcons.Create(value, IconSize);
            if (Content is ConnectingIndicator connecting) connecting.Dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, this.GetObservable(ForegroundProperty));
            if (Content is LoadingSpinner spinner)
                foreach (var arc in spinner.Children.OfType<Avalonia.Controls.Shapes.Path>()) arc.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetObservable(ForegroundProperty));
        }
    }
    public string Label { set { if (label == value) return; label = value; ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); } }
    public IconButton() { Padding = new Thickness(4); MinHeight = OperatingSystem.IsAndroid() ? 44 : 28; MinWidth = OperatingSystem.IsAndroid() ? 44 : 28; Icon = "copy"; }
}
