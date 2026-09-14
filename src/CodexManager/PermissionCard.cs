using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class PermissionCard : Border
{
    public PermissionCard(JsonObject request, Func<string?, Task> respond)
    {
        Padding = new Thickness(12); Margin = new Thickness(8, 4); CornerRadius = new CornerRadius(5); BorderThickness = new Thickness(1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("AppBorder")); this.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Permission needed", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new ScrollViewer { MaxHeight = 150, Content = new SelectableTextBlock { Text = request["toolCall"]?.ToJsonString() ?? "The agent is asking for permission.", TextWrapping = TextWrapping.Wrap } });
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var option in request["options"]?.AsArray() ?? [])
        {
            var id = option?["optionId"]?.GetValue<string>();
            var button = new Button { Content = option?["name"]?.GetValue<string>() ?? id, Margin = new Thickness(0, 0, 6, 4), MinHeight = 40 };
            button.Click += async (_, _) =>
            {
                buttons.IsEnabled = false;
                try { await respond(id); }
                catch (Exception error) { errorText.Text = AppDiagnostics.Message("Could not answer permission", error); buttons.IsEnabled = true; }
            };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); panel.Children.Add(errorText); Child = panel;
    }
}
