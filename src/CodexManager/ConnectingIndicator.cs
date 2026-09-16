using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;

namespace CodexManager;

public sealed class ConnectingIndicator : Grid
{
    private double phase;
    public Ellipse Dot { get; } = new() { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public ConnectingIndicator()
    {
        Width = Height = 14; IsHitTestVisible = false;
        Dot.Bind(Shape.FillProperty, this.GetResourceObservable("AppAccent")); Children.Add(Dot);
        var scale = new Avalonia.Media.ScaleTransform(); Dot.RenderTransform = scale; Dot.RenderTransformOrigin = RelativePoint.Center;
        _ = new VisibleAnimation(this, () => { phase += .3; scale.ScaleX = scale.ScaleY = .65 + .35 * (1 + Math.Sin(phase)) / 2; });
    }
    public static bool IsConnecting(string status) => status.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Recovering connection", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Loading", StringComparison.OrdinalIgnoreCase);
}
