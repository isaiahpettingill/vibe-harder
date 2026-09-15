using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Threading;

namespace CodexManager;

public sealed class ConnectingIndicator : Grid
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool attached;
    private double phase;
    public Ellipse Dot { get; } = new() { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public ConnectingIndicator()
    {
        Width = Height = 14; IsHitTestVisible = false;
        Dot.Bind(Shape.FillProperty, this.GetResourceObservable("AppAccent")); Children.Add(Dot);
        timer.Tick += (_, _) => { phase += .3; Dot.Opacity = .3 + .7 * (1 + Math.Sin(phase)) / 2; };
        AttachedToVisualTree += (_, _) => { attached = true; UpdateAnimation(); };
        DetachedFromVisualTree += (_, _) => { attached = false; timer.Stop(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateAnimation(); };
    }
    private void UpdateAnimation() { if (attached && IsVisible) timer.Start(); else timer.Stop(); }
    public static bool IsConnecting(string status) => new[] { "Connecting", "Reconnecting", "Recovering connection", "Loading" }.Any(prefix => status.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
