using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodexManager;

// One UI clock for small indicators; hidden/clipped/detached controls leave it.
public sealed class VisibleAnimation
{
    private static readonly HashSet<VisibleAnimation> active = [];
    private static readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Control owner;
    private readonly Action tick;
    private readonly double interval;
    private readonly List<Visual> ancestors = [];
    private bool attached, inViewport = true, enabled = true;
    private long nextTick;
    public bool Enabled { get => enabled; set { enabled = value; Update(); } }
    public static int ActiveCount => active.Count;
    static VisibleAnimation()
    {
        clock.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var animation in active.ToArray())
                if (now >= animation.nextTick && animation.owner.IsEffectivelyVisible)
                { animation.nextTick = now + (long)(animation.interval * Stopwatch.Frequency); animation.tick(); }
        };
    }
    public VisibleAnimation(Control owner, Action tick, int milliseconds = 60)
    {
        this.owner = owner; this.tick = tick; interval = milliseconds / 1000d;
        owner.AttachedToVisualTree += (_, _) =>
        {
            attached = true; inViewport = true;
            ancestors.Add(owner); ancestors.AddRange(owner.GetVisualAncestors());
            foreach (var visual in ancestors) visual.PropertyChanged += VisibilityChanged;
            Update();
        };
        owner.DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            foreach (var visual in ancestors) visual.PropertyChanged -= VisibilityChanged;
            ancestors.Clear(); Update();
        };
        owner.EffectiveViewportChanged += (_, e) => { inViewport = e.EffectiveViewport.Intersects(new Rect(owner.Bounds.Size)); Update(); };
    }
    private void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == Visual.IsVisibleProperty) Update(); }
    private void Update()
    {
        if (attached && enabled && inViewport && owner.IsEffectivelyVisible) active.Add(this); else active.Remove(this);
        if (active.Count > 0) clock.Start(); else clock.Stop();
    }
}
