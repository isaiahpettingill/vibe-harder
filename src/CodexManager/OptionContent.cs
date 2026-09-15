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
        var icon = IconFor(option, provider);
        if (ModelPicker.IsModel(option) && provider is AgentProvider.Dirac or AgentProvider.Pi)
            Children.Add(new Image { Source = BrandAssets.Provider(provider.Value), Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center });
        else Children.Add(AppIcons.Create(icon));
        Children.Add(new TextBlock { Text = option.Values.FirstOrDefault(v => v.Value == option.Current)?.Name ?? option.Current, MaxWidth = 135, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(AppIcons.Create("chevron-down", 8));
    }
    public static string IconFor(SessionConfig option, AgentProvider? provider = null)
    {
        if (ModelPicker.IsModel(option)) return provider == AgentProvider.Codex ? "openai" : provider == AgentProvider.Claude ? "claude" : "agent";
        var name = (option.Id + " " + option.Name).ToLowerInvariant();
        if (name.Contains("yolo")) return "yolo";
        if (name.Contains("auto_approve") || name.Contains("approve for me")) return "auto-approve";
        if (name.Contains("provider")) return "provider";
        if (name.Contains("tier") || name.Contains("speed") || name.Contains("fast")) return "speed";
        if (name.Contains("budget") || name.Contains("token")) return "budget";
        if (name.Contains("think") || name.Contains("reason") || name.Contains("effort")) return "reasoning";
        if (name.Contains("approv") || name.Contains("permission") || name.Contains("access")) return "permission";
        if (name.Contains("mode") || name.Contains("primary_agent")) return "mode";
        return "settings";
    }

}
