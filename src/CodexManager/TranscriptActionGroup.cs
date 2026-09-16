using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace CodexManager;

public sealed class TranscriptActionGroup : StackPanel
{
    private readonly IReadOnlyList<Message> messages;
    private readonly IDataTemplate? template;
    private readonly StackPanel details = new() { Margin = new(12, 0, 0, 0) };
    private readonly Button toggle;
    public static bool IsAction(object? item) => item is Message { Role: "tool" or "thought" or "plan" };
    public TranscriptActionGroup(IReadOnlyList<Message> messages, IDataTemplate? template)
    {
        this.messages = messages; this.template = template;
        toggle = new Button { Name = "ToggleActionGroup", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        toggle.Click += (_, _) => { messages[0].ActionGroupExpanded = !messages[0].ActionGroupExpanded; Refresh(); };
        Children.Add(toggle); Children.Add(details);
        AttachedToVisualTree += (_, _) => Refresh();
        DetachedFromVisualTree += (_, _) => details.Children.Clear();
        Refresh();
    }
    public void Expand() { messages[0].ActionGroupExpanded = true; Refresh(); }
    private void Refresh()
    {
        var expanded = messages[0].ActionGroupExpanded;
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(AppIcons.Create(expanded ? "chevron-down" : "chevron-right", 10));
        label.Children.Add(new TextBlock { Text = $"Actions & thinking ({messages.Count})", FontSize = 11, Classes = { "muted" } });
        toggle.Content = label;
        details.IsVisible = expanded;
        if (!expanded) { details.Children.Clear(); return; }
        if (details.Children.Count != 0) return;
        foreach (var message in messages)
        {
            var child = template?.Build(message) ?? new MessageView { Message = message };
            child.DataContext = message; details.Children.Add(child);
        }
    }
}
