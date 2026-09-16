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
    private readonly Button toggle = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 11 };
    private readonly ScrollViewer details;
    private bool expanded;
    private bool attached;
    private readonly IconButton history = new() { Icon = "more", IconSize = 11, Label = "Message actions", VerticalAlignment = VerticalAlignment.Top };
    public bool IsExpandedOutput => IsOutput && expanded;
    private bool IsOutput => Message?.Role is "tool" or "thought" or "plan";
    static MessageView() => MessageProperty.Changed.AddClassHandler<MessageView>((view, args) => view.Change(args.OldValue as Message));
    public MessageView()
    {
        toggle.Content = title; toggle.Click += (_, _) => { expanded = !expanded; if (Message is not null) Message.OutputExpanded = expanded; Refresh(); };
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(toggle); Grid.SetColumn(history, 1); header.Children.Add(history);
        history.Click += async (_, _) =>
        {
            if (Message is not { } message) return;
            if (this.GetVisualAncestors().OfType<RemoteView>().FirstOrDefault() is { } remote) await remote.ShowHistoryActions(history, message);
            else if (this.GetVisualAncestors().OfType<MainView>().FirstOrDefault() is { } main) await main.ShowHistoryActions(history, message);
        };
        details = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Content = new StackPanel { Spacing = 6, Children = { header, details } };
    }
    private void Change(Message? old)
    {
        if (old is not null) old.PropertyChanged -= MessageChanged;
        if (attached && Message is not null) Message.PropertyChanged += MessageChanged;
        expanded = !IsOutput || Message?.OutputExpanded == true; Refresh();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; if (Message is not null) Message.PropertyChanged += MessageChanged; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; details.Content = null; body = null; if (Message is not null) Message.PropertyChanged -= MessageChanged; base.OnDetachedFromVisualTree(e); }
    private void MessageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    public void Collapse() { if (IsOutput) { expanded = false; if (Message is not null) Message.OutputExpanded = false; Refresh(); } }
    public void Expand() { if (IsOutput) { expanded = true; if (Message is not null) Message.OutputExpanded = true; Refresh(); } }
    private void Refresh()
    {
        var legacyResume = Message is { Role: "user", Attachments.Count: 0 } && string.IsNullOrWhiteSpace(Message.Text);
        history.IsVisible = !legacyResume && Message?.Role is "user" or "assistant" or "tool";
        title.Foreground = this.TryFindResource(IsOutput ? "AppMuted" : "AppAccent", out var brush) ? brush as IBrush : null;
        var text = Message?.Text ?? "";
        var end = text.IndexOf('\n');
        var preview = text[..Math.Min(101, end < 0 ? text.Length : end)];
        if (preview.Length > 100) preview = preview[..100] + "…";
        title.Text = IsOutput ? (expanded ? "▾ " : "▸ ") + (Message?.Role == "plan" ? "Plan" : Message?.Role == "thought" ? "Thinking" : preview.Length > 0 ? preview : "Tool output") : legacyResume ? "SESSION" : Message?.Label;
        toggle.IsHitTestVisible = IsOutput; toggle.Focusable = IsOutput;
        details.IsVisible = !IsOutput || expanded;
        details.MaxHeight = IsOutput ? 420 : double.PositiveInfinity;
        if (details.IsVisible && attached)
        {
            body ??= new ChatMarkdown { Muted = IsOutput };
            details.Content = body;
            body.Text = legacyResume ? "Chat auto-resumed after unexpected restart" : Message?.Text ?? "";
        }
        else { details.Content = null; body = null; }
    }
}
