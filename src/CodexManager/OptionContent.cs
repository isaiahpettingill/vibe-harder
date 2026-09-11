using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class OptionContent : StackPanel
{
    public OptionContent(SessionConfig option)
    {
        Orientation = Orientation.Horizontal; Spacing = 4;
        var name = (option.Id + " " + option.Name).ToLowerInvariant();
        var geometry = name.Contains("model") ? "M2,3 L14,3 L14,13 L2,13 Z M5,6 L11,6 M5,10 L9,10" :
            name.Contains("think") || name.Contains("reason") ? "M5,11 L5,9 C0,3 5,1 8,1 C14,1 16,5 11,9 L11,11 Z M5,14 L11,14" :
            name.Contains("fast") || name.Contains("speed") ? "M9,1 L3,9 L8,9 L7,15 L14,6 L9,6 Z" :
            name.Contains("approv") || name.Contains("permission") ? "M8,1 L14,3 L14,8 L8,15 L2,8 L2,3 Z M5,7 L7,9 L11,5" :
            "M2,12 L11,3 L14,6 L5,15 L2,15 Z";
        Children.Add(Path(geometry));
        Children.Add(new TextBlock { Text = option.Values.FirstOrDefault(v => v.Value == option.Current)?.Name ?? option.Current, MaxWidth = 135, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(Path("M4,6 L8,10 L12,6"));
    }
    private static Control Path(string data) => new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Width = 13, Height = 13,  StrokeThickness = 1.3, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
}
