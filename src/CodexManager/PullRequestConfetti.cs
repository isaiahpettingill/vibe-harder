using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexManager;

public sealed class PullRequestConfetti : Control
{
    private const double Duration = 1.5;
    private const int FadeSteps = 12;
    private static readonly IBrush[][] Palette = Enumerable.Range(1, FadeSteps).Select(level =>
        new[] { Colors.Gold, Colors.DeepPink, Colors.DeepSkyBlue, Colors.MediumSpringGreen, Colors.MediumPurple }
            .Select(color => (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color, (double)level / FadeSteps)).ToArray()).ToArray();
    private readonly record struct Piece(double X, double Speed, double Drift, double Phase);
    private readonly Piece[] pieces = Enumerable.Range(0, 48).Select(_ => new Piece(Random.Shared.NextDouble(),
        0.6 + Random.Shared.NextDouble() * 0.55, Random.Shared.NextDouble() * 60 - 30,
        Random.Shared.NextDouble() * Math.PI * 2)).ToArray();
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(32) };
    public PullRequestConfetti()
    {
        IsHitTestVisible = false; ClipToBounds = true; ZIndex = 2000;
        timer.Tick += (_, _) =>
        {
            if (elapsed.Elapsed.TotalSeconds >= Duration) { timer.Stop(); if (Parent is Panel panel) panel.Children.Remove(this); }
            else InvalidateVisual();
        };
        AttachedToVisualTree += (_, _) => { elapsed.Restart(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => { timer.Stop(); elapsed.Stop(); };
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var seconds = elapsed.Elapsed.TotalSeconds;
        if (seconds >= Duration) return;
        // Fade each piece instead of compositing an opacity layer over the whole view.
        var fade = Math.Clamp((int)Math.Ceiling((Duration - seconds) * FadeSteps / 0.35) - 1, 0, FadeSteps - 1);
        var brushes = Palette[fade];
        var (width, height) = Bounds.Size;
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var y = -10 - piece.Phase * 15 + seconds * height * piece.Speed;
            if (y < -9 || y >= height) continue;
            var x = piece.X * width + Math.Sin(seconds * 3 + piece.Phase) * 25 + piece.Drift * seconds;
            if (x < -5 || x >= width) continue;
            context.DrawRectangle(brushes[i % brushes.Length], null, new Rect(x, y, 5, 9));
        }
    }
}
