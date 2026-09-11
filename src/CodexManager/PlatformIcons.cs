using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace CodexManager;

public static class PlatformIcons
{
    public static string Identify(string? distro)
    {
        if (string.IsNullOrWhiteSpace(distro))
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "apple";
            try { distro = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("ID=", StringComparison.Ordinal))?[3..].Trim('"'); } catch (IOException) { }
            if (string.IsNullOrWhiteSpace(distro)) return "linux";
        }
        var name = distro.ToLowerInvariant();
        foreach (var (match, icon) in new[] { ("alma", "almalinux"), ("rocky", "rockylinux"), ("centos", "centos"), ("rhel", "redhat"), ("redhat", "redhat"), ("red hat", "redhat"), ("gentoo", "gentoo"), ("void", "voidlinux"), ("manjaro", "manjaro"), ("cachy", "cachyos"), ("mint", "linuxmint"), ("ubuntu", "ubuntu"), ("debian", "debian"), ("fedora", "fedora"), ("arch", "archlinux"), ("suse", "opensuse") })
            if (name.Contains(match, StringComparison.Ordinal)) return icon;
        return "linux";
    }
    public static Image For(Workspace workspace)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://VibeHarder/Assets/Platforms/{Identify(workspace.Distro)}.svg"));
        var svg = XDocument.Load(stream).Root!;
        var bounds = svg.Attribute("viewBox")!.Value.Split(' ').Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(bounds[0], bounds[1], bounds[2], bounds[3])) });
        foreach (var path in svg.Descendants().Where(n => n.Name.LocalName == "path"))
            drawing.Children.Add(new GeometryDrawing { Brush = (IBrush)Application.Current!.Resources["AppText"]!, Geometry = Geometry.Parse(path.Attribute("d")!.Value) });
        return new Image { Source = new DrawingImage(drawing), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    }
}
