using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace CodexManager;

public static class BrandAssets
{
    private static readonly Dictionary<AgentProvider, DrawingImage> Images = [];

    public static DrawingImage Provider(AgentProvider provider)
    {
        if (Images.TryGetValue(provider, out var cached)) return cached;
        var file = provider switch { AgentProvider.Codex => "openai", AgentProvider.Claude => "anthropic", _ => "opencode" };
        using var stream = AssetLoader.Open(new Uri($"avares://VibeHarder.UI/Assets/Brands/{file}.svg"));
        var svg = XDocument.Load(stream).Root!;
        var bounds = svg.Attribute("viewBox")!.Value.Split(' ').Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(bounds[0], bounds[1], bounds[2], bounds[3])) });
        // These bundled logos use path geometry only. Their masks/clip paths are
        // identical to the viewBox and don't modify the visible paths.
        foreach (var path in svg.Descendants().Where(n => n.Name.LocalName == "path" && !n.Ancestors().Any(a => a.Name.LocalName is "mask" or "defs")))
        {
            var fill = path.Attribute("fill")?.Value ?? "#F1ECEC";
            if (fill == "currentColor") fill = "#F1ECEC";
            drawing.Children.Add(new GeometryDrawing { Brush = fill is "#F1ECEC" or "#fff" or "#ffffff" or "white" ? (IBrush)Application.Current!.Resources["AppText"]! : Brush.Parse(fill), Geometry = Geometry.Parse(path.Attribute("d")!.Value) });
        }
        return Images[provider] = new DrawingImage(drawing);
    }
}
