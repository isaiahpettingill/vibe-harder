using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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
    public bool IsExpandedOutput => IsOutput && expanded;
    private bool IsOutput => Message?.Role is "tool" or "thought";
    static MessageView() => MessageProperty.Changed.AddClassHandler<MessageView>((view, args) => view.Change(args.OldValue as Message));
    public MessageView()
    {
        toggle.Content = title; toggle.Click += (_, _) => { expanded = !expanded; if (Message is not null) Message.OutputExpanded = expanded; Refresh(); };
        var copy = new IconButton { Label = "Copy complete message with formatting" };
        ToolTip.SetTip(copy, "Copy complete message with formatting");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            var text = Message?.Text ?? "";
            var html = await Task.Run(() => Markdig.Markdown.ToHtml(text));
            await RichClipboard.Set(clipboard, text, html);
        };
        var header = new Grid { ColumnDefinitions = new("*,Auto") }; header.Children.Add(toggle); Grid.SetColumn(copy, 1); header.Children.Add(copy);
        details = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
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
    private void Refresh()
    {
        title.Foreground = this.TryFindResource(IsOutput ? "AppMuted" : "AppAccent", out var brush) ? brush as IBrush : null;
        var text = Message?.Text ?? "";
        var end = text.IndexOf('\n');
        var preview = text[..Math.Min(101, end < 0 ? text.Length : end)];
        if (preview.Length > 100) preview = preview[..100] + "…";
        title.Text = IsOutput ? (expanded ? "▾ " : "▸ ") + (Message?.Role == "thought" ? "Thinking" : preview.Length > 0 ? preview : "Tool output") : Message?.Label;
        toggle.IsHitTestVisible = IsOutput; toggle.Focusable = IsOutput;
        details.IsVisible = !IsOutput || expanded;
        details.MaxHeight = IsOutput ? 420 : double.PositiveInfinity;
        if (details.IsVisible && attached)
        {
            body ??= new ChatMarkdown { Muted = IsOutput };
            details.Content = body;
            body.Text = Message?.Text ?? "";
        }
        else { details.Content = null; body = null; }
    }
}
