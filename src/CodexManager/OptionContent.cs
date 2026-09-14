using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class OptionContent : StackPanel
{
    public OptionContent(SessionConfig option, AgentProvider? provider = null)
    {
        Orientation = Orientation.Horizontal; Spacing = 4;
        var name = (option.Id + " " + option.Name).ToLowerInvariant();
        var icon = ModelPicker.IsModel(option) ? provider == AgentProvider.Codex ? "openai" : provider == AgentProvider.Claude ? "claude" : "agent" : name.Contains("think") || name.Contains("reason") ? "reasoning" :
            name.Contains("fast") || name.Contains("speed") ? "speed" : name.Contains("approv") || name.Contains("permission") ? "permission" : "edit";
        Children.Add(AppIcons.Create(icon));
        Children.Add(new TextBlock { Text = option.Values.FirstOrDefault(v => v.Value == option.Current)?.Name ?? option.Current, MaxWidth = 135, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(AppIcons.Create("chevron-down", 8));
    }
}
