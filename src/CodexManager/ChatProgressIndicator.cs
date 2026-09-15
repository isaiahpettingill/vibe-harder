using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Threading;

namespace CodexManager;

public sealed class ChatProgressIndicator : TextBlock
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool attached;
    private int phase;
    public ChatProgressIndicator()
    {
        Text = "·"; FontSize = 20; Height = 28; Width = 40;
        Margin = new Thickness(8, 0); IsVisible = false; IsHitTestVisible = false;
        AutomationProperties.SetName(this, "Agent working");
        timer.Tick += (_, _) => { phase = (phase + 1) % 3; Text = new string('·', phase + 1); };
        AttachedToVisualTree += (_, _) => { attached = true; UpdateAnimation(); };
        DetachedFromVisualTree += (_, _) => { attached = false; timer.Stop(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateAnimation(); };
    }
    private void UpdateAnimation()
    {
        if (attached && IsVisible) timer.Start();
        else { timer.Stop(); phase = 0; Text = "·"; }
    }
}
