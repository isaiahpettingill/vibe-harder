using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodexManager;

// Touch has no right click: a finger held still opens what a right click would. Avalonia's own
// Holding gesture gives up after 4 px of movement, which a resting finger easily exceeds.
public static class LongPress
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);
    private const double Slop = 10;

    public static void Attach(Control control, Action<Point, IPointer> held, Func<PointerPressedEventArgs, bool>? accepts = null)
    {
        IDisposable? timer = null; Point start = default;
        void Cancel() { timer?.Dispose(); timer = null; }
        control.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            Cancel();
            if (e.Pointer.Type == PointerType.Mouse || accepts?.Invoke(e) == false) return;
            start = e.GetPosition(control); var pointer = e.Pointer;
            timer = DispatcherTimer.RunOnce(() =>
            {
                timer = null;
                // A scroll that took the pointer over is not a hold, even before the finger moved far.
                if (pointer.Captured is Visual captor && captor != control && !control.IsVisualAncestorOf(captor)) return;
                held(start, pointer);
            }, Delay);
        }, RoutingStrategies.Tunnel, true);
        control.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (timer is null) return;
            var delta = e.GetPosition(control) - start;
            if (delta.X * delta.X + delta.Y * delta.Y > Slop * Slop) Cancel();
        }, RoutingStrategies.Tunnel, true);
        control.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Cancel(), RoutingStrategies.Tunnel, true);
        control.DetachedFromVisualTree += (_, _) => Cancel();
    }
}
