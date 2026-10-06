using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace CodexManager;

// A question from the agent. Choice questions show every option with its description, and the
// free-text companion that AIR agents attach to a question ("Other") becomes that question's
// write-in answer instead of an unrelated field.
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
        var properties = schema["properties"]!.AsObject();
        var companions = properties.Where(p => p.Value is JsonObject f && ElicitationForm.CustomAnswerFor(f) is { } question && properties.ContainsKey(question))
            .ToDictionary(p => ElicitationForm.CustomAnswerFor((JsonObject)p.Value!)!, p => (Name: p.Key, Field: (JsonObject)p.Value!));
        var group = 0;
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject field || companions.Values.Any(c => c.Name == name)) continue;
            var label = ElicitationForm.Label(name, field);
            var isRequired = required?.Any(item => item?.GetValue<string>() == name) == true;
            var section = new StackPanel { Spacing = 4 };
            panel.Children.Add(section);
            section.Children.Add(new TextBlock { Text = label + (isRequired ? " *" : ""), FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
            if (field["description"]?.GetValue<string>() is { Length: > 0 } help)
                section.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var type = field["type"]?.GetValue<string>();
            var choices = ElicitationForm.Choices(field);
            TextBox? Other(string placeholder)
            {
                if (!companions.TryGetValue(name, out var companion)) return null;
                var other = new TextBox { Name = "OtherAnswer", PlaceholderText = placeholder, MinWidth = 180, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
                inputs.Add((companion.Name, () => string.IsNullOrWhiteSpace(other.Text) ? null : JsonValue.Create(other.Text.Trim())));
                return other;
            }
            if (type == "array" && choices.Count > 0)
            {
                var defaults = field["default"] as JsonArray;
                var checkboxes = choices.Select(choice =>
                {
                    var checkbox = new CheckBox { Content = Option(choice.Label, choice.Description) };
                    checkbox.IsChecked = defaults?.Any(value => JsonNode.DeepEquals(value, choice.Value)) == true;
                    section.Children.Add(checkbox);
                    return (choice.Value, Checkbox: checkbox);
                }).ToArray();
                inputs.Add((name, () => new JsonArray(checkboxes.Where(c => c.Checkbox.IsChecked == true).Select(c => c.Value.DeepClone()).ToArray())));
                if (Other("Other — add your own answer (optional)") is { } other) section.Children.Add(other);
            }
            else if (type == "string" && choices.Count > 0)
            {
                var groupName = "question-" + group++;
                var selected = Math.Max(0, Array.FindIndex(choices.ToArray(), c => JsonNode.DeepEquals(c.Value, field["default"])));
                var radios = choices.Select((choice, index) =>
                {
                    var radio = new RadioButton { GroupName = groupName, Content = Option(choice.Label, choice.Description), IsChecked = index == selected };
                    section.Children.Add(radio);
                    return radio;
                }).ToArray();
                string? Picked() => Array.FindIndex(radios, r => r.IsChecked == true) is >= 0 and var index ? choices[index].Label : null;
                JsonNode? Choice() => Array.FindIndex(radios, r => r.IsChecked == true) is >= 0 and var index ? choices[index].Value.DeepClone() : null;
                if (companions.TryGetValue(name, out var companion))
                {
                    // The write-in is its own choice; next to a picked option its text is a note.
                    var write = new RadioButton { GroupName = groupName, Content = "Other" };
                    var other = new TextBox { Name = "OtherAnswer", PlaceholderText = "Type your own answer, or a note for the option above", MinWidth = 180, TextWrapping = TextWrapping.Wrap };
                    section.Children.Add(write); section.Children.Add(other);
                    // Typing selects the write-in unless an option was picked on purpose; then it is a note.
                    var picked = false;
                    foreach (var radio in radios)
                    {
                        radio.Click += (_, _) => picked = true;
                        radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true && radio != radios[selected]) picked = true; };
                    }
                    other.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !string.IsNullOrWhiteSpace(other.Text) && !picked) write.IsChecked = true; };
                    string? Text() => string.IsNullOrWhiteSpace(other.Text) ? null : other.Text.Trim();
                    // Codex reads a typed choice as the answer; Claude reads its companion field.
                    var typed = ElicitationForm.TakesTypedAnswer(properties, name);
                    inputs.Add((name, () => write.IsChecked == true ? typed && Text() is { } answer ? JsonValue.Create(answer) : null : Choice()));
                    inputs.Add((companion.Name, () => write.IsChecked == true && typed ? null : Text() is { } note ? JsonValue.Create(note) : null));
                }
                else inputs.Add((name, Choice));
            }
            else if (type == "boolean")
            {
                var check = new CheckBox { IsChecked = field["default"]?.GetValue<bool>() == true, Content = "Yes" };
                section.Children.Add(check); inputs.Add((name, () => JsonValue.Create(check.IsChecked == true)));
            }
            else if (type is "string" or "integer" or "number")
            {
                var input = new TextBox { Text = field["default"] is JsonValue value ? value.ToString() : "", PlaceholderText = type == "string" ? label : "Enter a number", MinWidth = 180, TextWrapping = TextWrapping.Wrap };
                section.Children.Add(input);
                inputs.Add((name, () =>
                {
                    if (string.IsNullOrWhiteSpace(input.Text) && !isRequired) return null;
                    if (type == "integer") return long.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? JsonValue.Create(integer) : JsonValue.Create(input.Text);
                    if (type == "number") return double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(input.Text);
                    return JsonValue.Create(input.Text ?? "");
                }
                ));
            }
            else section.Children.Add(new TextBlock { Text = "This field type is not supported." });
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
        // Actions sit on the right with the primary one last.
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var (label, answer) in new[] { ("Cancel", ElicitationForm.Cancel()), ("Decline", ElicitationForm.Decline()) })
        {
            var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 4), MinHeight = 40 };
            button.Click += async (_, _) => await Reply(answer);
            buttons.Children.Add(button);
        }
        submit.Margin = new Thickness(6, 0, 0, 4);
        buttons.Children.Add(submit);
        // Enter sends the answer from anywhere in the card; Ctrl+Enter adds a line in a text field.
        // Buttons handle their own Enter first, so Enter on Decline still declines.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || !buttons.IsEnabled) return;
            if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
            {
                if (e.Source is TextBox { AcceptsReturn: true } box) { box.SelectedText = "\n"; e.Handled = true; }
                return;
            }
            if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None || e.Source is Button and not Avalonia.Controls.Primitives.ToggleButton) return;
            e.Handled = true; submit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        foreach (var box in panel.GetLogicalDescendants().OfType<TextBox>()) box.AcceptsReturn = true;
        panel.Children.Add(buttons); panel.Children.Add(errorText); Child = panel;
    }
    private static Control Option(string label, string? description)
    {
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description is { Length: > 0 }) text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });
        return text;
    }
}
