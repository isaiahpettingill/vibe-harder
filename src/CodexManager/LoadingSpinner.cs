using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace CodexManager;

public sealed class LoadingSpinner : Avalonia.Controls.Grid
{
    internal static readonly Geometry Arc = Geometry.Parse("M7,1 A6,6 0 1 1 1,7");
    public LoadingSpinner()
    {
        Width = Height = 14; IsHitTestVisible = false;
        var rotation = new RotateTransform();
        var arc = new Avalonia.Controls.Shapes.Path { Data = Arc, StrokeThickness = 1.6, RenderTransform = rotation, RenderTransformOrigin = RelativePoint.Center };
        arc.Bind(Shape.StrokeProperty, this.GetResourceObservable("AppAccent")); Children.Add(arc);
        _ = new VisibleAnimation(this, () => rotation.Angle = (rotation.Angle + 24) % 360);
    }
}
