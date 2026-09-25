using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class ElicitationCard : Border
{
    public ElicitationCard(JsonObject request, Func<JsonObject, Task> respond)
    {
        var schema = ElicitationForm.Schema(request);
        Padding = new Thickness(12); Margin = new Thickness(8, 4); CornerRadius = new CornerRadius(5); BorderThickness = new Thickness(1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        this.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = schema["title"]?.GetValue<string>() ?? "Question from agent", FontWeight = FontWeight.SemiBold });
        if (request["message"]?.GetValue<string>() is { Length: > 0 } message)
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (schema["description"]?.GetValue<string>() is { Length: > 0 } description)
            panel.Children.Add(new SelectableTextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        var inputs = new List<(string Name, Func<JsonNode?> Read)>();
        var required = schema["required"] as JsonArray;
        foreach (var (name, node) in schema["properties"]!.AsObject())
        {
            if (node is not JsonObject field) continue;
            var label = ElicitationForm.Label(name, field);
            var isRequired = required?.Any(item => item?.GetValue<string>() == name) == true;
            panel.Children.Add(new TextBlock { Text = label + (isRequired ? " *" : ""), FontWeight = FontWeight.Medium });
            if (field["description"]?.GetValue<string>() is { Length: > 0 } help)
                panel.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var type = field["type"]?.GetValue<string>();
            var choices = ElicitationForm.Choices(field);
            if (type == "array" && choices.Count > 0)
            {
                var defaults = field["default"] as JsonArray;
                var checkboxes = choices.Select(choice =>
                {
                    var checkbox = new CheckBox { Content = choice.Label, IsChecked = defaults?.Any(value => JsonNode.DeepEquals(value, choice.Value)) == true };
                    panel.Children.Add(checkbox);
                    if (choice.Description is { Length: > 0 } detail) panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                    return (choice.Value, Checkbox: checkbox);
                }).ToArray();
                inputs.Add((name, () => new JsonArray(checkboxes.Where(c => c.Checkbox.IsChecked == true).Select(c => c.Value.DeepClone()).ToArray())));
            }
            else if (type == "string" && choices.Count > 0)
            {
                var options = choices.Select(c => c.Label).ToArray();
                var select = new ComboBox { ItemsSource = options, MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
                select.SelectedIndex = Math.Max(0, Array.FindIndex(choices.ToArray(), c => JsonNode.DeepEquals(c.Value, field["default"])));
                panel.Children.Add(select);
                var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
                void Describe() => detail.Text = select.SelectedIndex >= 0 ? choices[select.SelectedIndex].Description : null;
                select.SelectionChanged += (_, _) => Describe(); Describe(); panel.Children.Add(detail);
                inputs.Add((name, () => select.SelectedIndex >= 0 ? choices[select.SelectedIndex].Value.DeepClone() : null));
            }
            else if (type == "boolean")
            {
                var check = new CheckBox { IsChecked = field["default"]?.GetValue<bool>() == true, Content = "Yes" };
                panel.Children.Add(check); inputs.Add((name, () => JsonValue.Create(check.IsChecked == true)));
            }
            else if (type is "string" or "integer" or "number")
            {
                var input = new TextBox { Text = field["default"] is JsonValue value ? value.ToString() : "", PlaceholderText = type == "string" ? label : "Enter a number", MinWidth = 180 };
                panel.Children.Add(input);
                inputs.Add((name, () =>
                {
                    if (string.IsNullOrWhiteSpace(input.Text) && !isRequired) return null;
                    if (type == "integer") return long.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? JsonValue.Create(integer) : JsonValue.Create(input.Text);
                    if (type == "number") return double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(input.Text);
                    return JsonValue.Create(input.Text ?? "");
                }
                ));
            }
            else panel.Children.Add(new TextBlock { Text = "This field type is not supported." });
        }
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        async Task Reply(JsonObject answer)
        {
            buttons.IsEnabled = false;
            try { await respond(answer); }
            catch (Exception error) { errorText.Text = AppDiagnostics.Message("Could not answer question", error); buttons.IsEnabled = true; }
        }
        var submit = new Button { Content = "Send answer", Margin = new Thickness(0, 0, 6, 4), MinHeight = 40, Classes = { "accent" } };
        submit.Click += async (_, _) =>
        {
            var content = new JsonObject();
            foreach (var (name, read) in inputs) if (read() is { } value) content[name] = value;
            if (ElicitationForm.Validate(request, content) is { } error) { errorText.Text = error; return; }
            await Reply(ElicitationForm.Accept(content));
        };
        buttons.Children.Add(submit);
        foreach (var (label, answer) in new[] { ("Decline", ElicitationForm.Decline()), ("Cancel", ElicitationForm.Cancel()) })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4), MinHeight = 40 };
            button.Click += async (_, _) => await Reply(answer);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); panel.Children.Add(errorText); Child = panel;
    }
}
