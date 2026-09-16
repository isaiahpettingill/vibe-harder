using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;

namespace CodexManager;

public sealed class ChatProgressIndicator : TextBlock
{
    private int phase;
    public ChatProgressIndicator()
    {
        Text = "·"; FontSize = 20; Height = 28; Width = 40;
        Margin = new Thickness(8, 0); IsVisible = false; IsHitTestVisible = false;
        AutomationProperties.SetName(this, "Agent working");
        _ = new VisibleAnimation(this, () => { phase = (phase + 1) % 3; Text = phase switch { 0 => "·", 1 => "··", _ => "···" }; }, 350);
    }
}
