using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

// An approval request. AIR agents add a heading, a reason, and whether a stray keystroke must not
// approve; the body shows what is being approved: a plan to review, a command, or the edit.
public sealed class PermissionCard : Border
{
    public PermissionCard(JsonObject request, Func<string?, Task> respond)
    {
        Padding = new Thickness(12); Margin = new Thickness(8, 4); CornerRadius = new CornerRadius(5); BorderThickness = new Thickness(1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("AppBorder")); this.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
        var presentation = Air.Meta(request["_meta"])?["permission"];
        var toolCall = request["toolCall"] as JsonObject;
        var input = toolCall?["rawInput"];
        var panel = new StackPanel { Spacing = 8 };
        var heading = Text(presentation?["title"]) ?? Text(toolCall?["title"]) ?? "Permission needed";
        panel.Children.Add(new SelectableTextBlock { Text = heading, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 6 });
        if (Text(presentation?["description"]) is { } reason)
            panel.Children.Add(new SelectableTextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        if (Body(toolCall, input, heading) is { } body) panel.Children.Add(body);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var options = (request["options"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        // With defaultToNo the decline is the default action, so it gets the emphasis and the focus.
        var safe = presentation?["defaultToNo"]?.GetValueKind() == JsonValueKind.True;
        var preferred = options.FirstOrDefault(o => Text(o["kind"])?.StartsWith(safe ? "reject" : "allow", StringComparison.Ordinal) == true);
        Button? focus = null;
        foreach (var option in options)
        {
            var id = Text(option["optionId"]);
            var button = new Button { Content = Text(option["name"]) ?? id, Margin = new Thickness(6, 0, 0, 4), MinHeight = 40 };
            if (ReferenceEquals(option, preferred)) { button.Classes.Add("accent"); focus = button; }
            if (Text(Air.Meta(option["_meta"])?["permission"]?["description"]) is { } help) ToolTip.SetTip(button, help);
            button.Click += async (_, _) =>
            {
                buttons.IsEnabled = false;
                try { await respond(id); }
                catch (Exception error) { errorText.Text = AppDiagnostics.Message("Could not answer permission", error); buttons.IsEnabled = true; }
            };
            buttons.Children.Add(button);
        }
        if (safe && focus is not null) Loaded += (_, _) => focus.Focus();
        panel.Children.Add(buttons); panel.Children.Add(errorText); Child = panel;
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text : null;

    private static Control? Body(JsonObject? toolCall, JsonNode? input, string heading)
    {
        // A plan review (Codex "Implement this plan?", Claude's ExitPlanMode) shows the plan itself.
        if (Text(input?["plan"]) is { } plan)
            return new ScrollViewer { MaxHeight = 360, Content = new ChatMarkdown { Text = plan } };
        if (input?["command"] is { } command)
        {
            var text = command is JsonArray parts ? string.Join(' ', parts.Select(p => p?.ToString())) : Text(command);
            if (text is not null && text != heading) return Code(text);
            if (text is not null) return null;
        }
        if (toolCall is not null)
        {
            // Edits and other details travel as tool call content, such as a diff or a patch.
            var call = new AcpToolCall();
            using (var document = JsonDocument.Parse(toolCall.ToJsonString())) call.Merge(document.RootElement);
            if (call.Content.Length > 0) return new ScrollViewer { MaxHeight = 320, Content = new ChatMarkdown { Text = call.Content, Muted = true } };
        }
        if (input is JsonObject { Count: > 0 } || input is JsonArray { Count: > 0 })
            return new ScrollViewer { MaxHeight = 150, Content = Code(input.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) };
        return toolCall is null ? new TextBlock { Text = "The agent is asking for permission.", TextWrapping = TextWrapping.Wrap } : null;
    }
    private static Control Code(string text)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.FontFamilyProperty, block.GetResourceObservable("CodeFont"));
        return new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(4), Child = block, [!Border.BackgroundProperty] = block.GetResourceObservable("AppBackground").ToBinding() };
    }
}
