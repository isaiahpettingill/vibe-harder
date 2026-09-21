using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CodexManager;

public sealed class MessageView : UserControl
{
    public static readonly StyledProperty<Message?> MessageProperty = AvaloniaProperty.Register<MessageView, Message?>(nameof(Message));
    public Message? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    private ChatMarkdown? body;
    private SubagentView? subagentView;
    private readonly Border frame = new() { Name = "MessageFrame" };
    private string? legacyText;
    private SubagentInfo? legacySubagent;
    private readonly Button toggle = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 11 };
    private readonly TextBlock timestamp = new() { Name = "MessageTimestamp", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private static readonly HashSet<MessageView> timestampViews = [];
    private static readonly Avalonia.Threading.DispatcherTimer timestampTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly ScrollViewer details;
    private bool expanded;
    private bool attached;
    private readonly IconButton history = new() { Icon = "more", IconSize = 11, Label = "Message actions", VerticalAlignment = VerticalAlignment.Top };
    public bool IsExpandedOutput => IsOutput && expanded;
    private bool IsOutput => Message?.Role is "tool" or "thought" or "plan";
    static MessageView()
    {
        MessageProperty.Changed.AddClassHandler<MessageView>((view, args) => view.Change(args.OldValue as Message));
        timestampTimer.Tick += (_, _) => { foreach (var view in timestampViews) view.UpdateTimestamp(); };
    }
    public MessageView()
    {
        toggle.Content = title; toggle.Click += (_, _) => { expanded = !expanded; if (Message is not null) Message.OutputExpanded = expanded; Refresh(); };
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
        header.Children.Add(toggle); Grid.SetColumn(history, 1); header.Children.Add(history);
        timestamp.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
        Grid.SetColumn(timestamp, 2); header.Children.Add(timestamp);
        history.Click += async (_, _) =>
        {
            if (Message is not { } message) return;
            if (this.GetVisualAncestors().OfType<RemoteView>().FirstOrDefault() is { } remote) await remote.ShowHistoryActions(history, message);
            else if (this.GetVisualAncestors().OfType<MainView>().FirstOrDefault() is { } main) await main.ShowHistoryActions(history, message);
        };
        details = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("AppAccent"));
        frame.Child = new StackPanel { Spacing = 6, Children = { header, details } };
        Content = frame;
    }
    private void Change(Message? old)
    {
        if (old is not null) old.PropertyChanged -= MessageChanged;
        if (attached && Message is not null) Message.PropertyChanged += MessageChanged;
        expanded = !IsOutput || Message?.OutputExpanded == true; Refresh();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; timestampViews.Add(this); timestampTimer.Start(); if (Message is not null) Message.PropertyChanged += MessageChanged; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; timestampViews.Remove(this); if (timestampViews.Count == 0) timestampTimer.Stop(); details.Content = null; body = null; if (Message is not null) Message.PropertyChanged -= MessageChanged; base.OnDetachedFromVisualTree(e); }
    private void UpdateTimestamp()
    {
        timestamp.Text = Message?.Timestamp is { } time ? MessageTime.Format(time, DateTimeOffset.UtcNow) : "";
        timestamp.IsVisible = Message?.Timestamp is not null;
    }
    private void MessageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    public void Collapse() { if (IsOutput) { expanded = false; if (Message is not null) Message.OutputExpanded = false; subagentView?.Collapse(); Refresh(); } }
    public void Expand() { if (IsOutput) { expanded = true; if (Message is not null) Message.OutputExpanded = true; Refresh(); } }
    private void Refresh()
    {
        UpdateTimestamp();
        var isUser = Message is { Role: "user" } && (!string.IsNullOrWhiteSpace(Message.Text) || Message.Attachments.Count > 0);
        frame.BorderThickness = isUser ? new Thickness(2, 0, 0, 0) : default;
        frame.Padding = isUser ? new Thickness(12, 8) : default;
        if (legacyText != Message?.Text)
        {
            legacyText = Message?.Text;
            legacySubagent = Message is { Role: "tool", Subagent: null } ? SubagentInfo.FromLegacy(Message.Text) : null;
        }
        var subagent = Message?.Subagent ?? legacySubagent;
        if (subagent is not null)
        {
            toggle.IsVisible = false; history.IsVisible = false; details.IsVisible = true; details.MaxHeight = 600;
            subagentView ??= new SubagentView(); subagentView.Update(subagent); details.Content = subagentView;
            subagentView.ExpansionChanged = value => { if (Message is not null) Message.OutputExpanded = value; };
            if (Message?.OutputExpanded == true) subagentView.Expand();
            body = null; return;
        }
        subagentView = null; toggle.IsVisible = true;
        var legacyResume = Message is { Role: "user", Attachments.Count: 0 } && string.IsNullOrWhiteSpace(Message.Text);
        var sessionNotice = Message?.Role == "system" || legacyResume;
        history.IsVisible = !legacyResume && !this.GetVisualAncestors().Any(v => v is SubagentView or SubagentInspector) && Message?.Role is "user" or "assistant" or "tool";
        title.Foreground = this.TryFindResource(IsOutput ? "AppMuted" : "AppAccent", out var brush) ? brush as IBrush : null;
        var text = Message?.Text ?? "";
        var end = text.IndexOf('\n');
        var preview = text[..Math.Min(101, end < 0 ? text.Length : end)];
        if (preview.Length > 100) preview = preview[..100] + "…";
        title.Text = IsOutput ? (expanded ? "▾ " : "▸ ") + (Message?.Role == "plan" ? "Plan" : Message?.Role == "thought" ? "Thinking" : preview.Length > 0 ? preview : "Tool output") : legacyResume ? "SESSION" : Message?.Label;
        if (Message?.Role == "assistant" && this.GetVisualAncestors().Any(v => v is SubagentView)) title.Text = "SUBAGENT";
        toggle.IsHitTestVisible = IsOutput; toggle.Focusable = IsOutput;
        details.IsVisible = !IsOutput || expanded;
        details.MaxHeight = IsOutput ? 420 : double.PositiveInfinity;
        if (details.IsVisible && attached)
        {
            if (body is null || body.SessionNotice != sessionNotice || body.Muted != IsOutput) body = new ChatMarkdown { Muted = IsOutput, SessionNotice = sessionNotice };
            details.Content = body;
            body.Text = legacyResume ? "Chat auto-resumed after unexpected restart" : Message?.Text ?? "";
        }
        else { details.Content = null; body = null; }
    }
}
