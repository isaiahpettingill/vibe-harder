using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexManager;

public sealed class LoadingSpinner : Avalonia.Controls.Grid
{
    public LoadingSpinner()
    {
        Width = Height = 14; IsHitTestVisible = false;
        var rotation = new RotateTransform();
        var arc = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M7,1 A6,6 0 1 1 1,7"), StrokeThickness = 1.6, RenderTransform = rotation, RenderTransformOrigin = RelativePoint.Center };
        arc.Bind(Shape.StrokeProperty, this.GetResourceObservable("AppAccent")); Children.Add(arc);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => rotation.Angle = (rotation.Angle + 24) % 360;
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }
}
