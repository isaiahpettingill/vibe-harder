using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CodexManager;

public sealed class SlashCommandOverlay : Popup
{
    public SlashCommandOverlay(TextBox composer, ListBox commands)
    {
        PlacementTarget = composer; Placement = PlacementMode.TopEdgeAlignedLeft;
        VerticalOffset = -6; IsLightDismissEnabled = true;
        commands.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true; commands.IsVisible = false; composer.Focus();
        }, RoutingStrategies.Tunnel);
        commands.MaxHeight = 220;
        commands.ItemTemplate = new FuncDataTemplate<SlashCommand>((command, _) => command is null ? null : new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "/" + command.Name + (command.Hint is { Length: > 0 } hint ? "  " + hint : ""), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = command.Description, IsVisible = command.Description.Length > 0, FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        var border = new Border { Padding = new Thickness(6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Commands", FontSize = 11, Margin = new Thickness(6, 2) }, commands } } };
        border.Bind(Border.BackgroundProperty, composer.GetResourceObservable("AppSurface"));
        border.Bind(Border.BorderBrushProperty, composer.GetResourceObservable("AppBorder")); Child = border;
        string? dismissedText = null;
        composer.TextChanged += (_, _) => { if (composer.Text != dismissedText) dismissedText = null; };
        commands.PropertyChanged += (_, args) =>
        {
            if (args.Property != IsVisibleProperty) return;
            if (commands.IsVisible && dismissedText == composer.Text) { commands.IsVisible = false; return; }
            if (commands.IsVisible) border.Width = Math.Min(420, Math.Max(220, composer.Bounds.Width));
            IsOpen = commands.IsVisible;
        };
        Closed += (_, _) => { dismissedText = composer.Text; commands.IsVisible = false; };
    }
}
