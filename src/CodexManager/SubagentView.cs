using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CodexManager;

public sealed class SubagentView : StackPanel
{
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Classes = { "muted" }, FontSize = 11 };
    private readonly Button toggle = new() { Name = "ExpandSubagent", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly IconButton fullscreen = new() { Name = "OpenSubagentFullscreen", Icon = "fullscreen", Label = "Open subagent fullscreen" };
    public bool InspectorMode { get => !fullscreen.IsVisible; set => fullscreen.IsVisible = !value; }
    private readonly StackPanel detail = new() { Spacing = 8, Margin = new Thickness(16, 4, 0, 4) };
    private readonly Dictionary<string, (Message Message, MessageView View)> activity = [];
    private readonly Message prompt = new() { Role = "tool", Text = "Task" };
    private readonly ChatMarkdown output = new();
    private readonly StackPanel steps = new() { Spacing = 6 };
    private SubagentInfo? info;
    private bool expanded;
    public Action<bool>? ExpansionChanged { get; set; }
    public SubagentView()
    {
        Spacing = 4;
        toggle.Content = new StackPanel { Spacing = 3, Children = { heading, status } };
        toggle.Click += (_, _) => { expanded = !expanded; ExpansionChanged?.Invoke(expanded); Refresh(); };
        var header = new Grid { ColumnDefinitions = new("*,Auto") }; header.Children.Add(toggle); Grid.SetColumn(fullscreen, 1); header.Children.Add(fullscreen);
        fullscreen.Click += (_, _) =>
        {
            var main = this.GetVisualAncestors().OfType<MainView>().FirstOrDefault();
            var parents = this.GetVisualAncestors().OfType<MessageView>().Reverse().Select(v => v.Message).OfType<Message>().ToArray();
            if (this.GetVisualAncestors().OfType<SubagentInspector>().FirstOrDefault() is { } inspector)
                main?.ShowSubagent(inspector.Root, inspector.Path.Concat(parents.Select(m => m.Id)).ToArray());
            else if (parents.Length > 0) main?.ShowSubagent(parents[0], parents.Skip(1).Select(m => m.Id).ToArray());
        };
        Children.Add(header); Children.Add(detail);
        DetachedFromVisualTree += (_, _) => { detail.Children.Clear(); steps.Children.Clear(); activity.Clear(); };
        AttachedToVisualTree += (_, _) => Refresh();
    }
    public void Update(SubagentInfo value) { info = value; Refresh(); }
    public void Expand() { expanded = true; Refresh(); }
    public void Collapse() { expanded = false; Refresh(); }
    private void Refresh()
    {
        if (info is null) return;
        heading.Text = (expanded ? "▾  " : "▸  ") + info.Title;
        var state = info.Status switch { "in_progress" or "running" => "Working", "completed" => "Completed", "failed" => "Failed", "cancelled" => "Cancelled", "disconnected" => "Disconnected", "pending" => "Pending", _ => info.Status };
        status.Text = string.Join(" · ", new[] { info.Agent, state, info.Activity.Length >= 128 ? "Showing latest 128 activity items" : "" }.Where(s => s.Length > 0));
        detail.IsVisible = expanded;
        if (!expanded) { detail.Children.Clear(); steps.Children.Clear(); activity.Clear(); return; }
        if (detail.Children.Count == 0)
        {
            detail.Children.Add(new MessageView { Message = prompt });
            detail.Children.Add(steps); detail.Children.Add(output);
        }
        prompt.Text = "Task\n\n" + info.Prompt;
        detail.Children[0].IsVisible = info.Prompt.Length > 0;
        output.Text = info.Output; output.IsVisible = info.Output.Length > 0;
        var keys = info.Activity.Select(e => e.Id).ToHashSet();
        foreach (var id in activity.Keys.Where(id => !keys.Contains(id)).ToArray()) { steps.Children.Remove(activity[id].View); activity.Remove(id); }
        foreach (var entry in info.Activity)
        {
            if (!activity.TryGetValue(entry.Id, out var item))
            {
                var message = new Message { Role = entry.Role, Id = entry.Id };
                item = (message, new MessageView { Message = message }); activity[entry.Id] = item; steps.Children.Add(item.View);
            }
            item.Message.Subagent = entry.Child; item.Message.Text = entry.Text;
        }
    }
}
