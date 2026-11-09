using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexManager;

public sealed class PullRequestConfetti : Control
{
    private readonly record struct Piece(double X, double Speed, double Drift, double Phase, IBrush Brush);
    private readonly Piece[] pieces = Enumerable.Range(0, 90).Select(i => new Piece(Random.Shared.NextDouble(),
        100 + Random.Shared.NextDouble() * 150, Random.Shared.NextDouble() * 60 - 30,
        Random.Shared.NextDouble() * Math.PI * 2,
        new SolidColorBrush(new[] { Colors.Gold, Colors.DeepPink, Colors.DeepSkyBlue, Colors.MediumSpringGreen, Colors.MediumPurple }[i % 5]))).ToArray();
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    public PullRequestConfetti()
    {
        IsHitTestVisible = false; ClipToBounds = true; ZIndex = 2000;
        timer.Tick += (_, _) =>
        {
            if (elapsed.Elapsed.TotalSeconds >= 4) { timer.Stop(); if (Parent is Panel panel) panel.Children.Remove(this); }
            else InvalidateVisual();
        };
        AttachedToVisualTree += (_, _) => { elapsed.Restart(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => { timer.Stop(); elapsed.Stop(); };
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var seconds = elapsed.Elapsed.TotalSeconds;
        using var opacity = context.PushOpacity(Math.Clamp(4 - seconds, 0, 1));
        foreach (var piece in pieces)
        {
            var x = piece.X * Bounds.Width + Math.Sin(seconds * 3 + piece.Phase) * 25 + piece.Drift * seconds;
            var y = -30 - piece.Phase * 30 + seconds * piece.Speed;
            context.DrawRectangle(piece.Brush, null, new Rect(x, y, 5, 9));
        }
    }
}
