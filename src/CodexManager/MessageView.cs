using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class MessageView : UserControl
{
    public static readonly StyledProperty<Message?> MessageProperty = AvaloniaProperty.Register<MessageView, Message?>(nameof(Message));
    public Message? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    private readonly ChatMarkdown body = new();
    private readonly Button toggle = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 11 };
    private readonly ScrollViewer details;
    private bool expanded;
    public bool IsExpandedOutput => IsOutput && expanded;
    private bool IsOutput => Message?.Role is "tool" or "thought";
    static MessageView() => MessageProperty.Changed.AddClassHandler<MessageView>((view, args) => view.Change(args.OldValue as Message));
    public MessageView()
    {
        toggle.Content = title; toggle.Click += (_, _) => { expanded = !expanded; Refresh(); };
        var copy = new IconButton { Label = "Copy complete message with formatting" };
        ToolTip.SetTip(copy, "Copy complete message with formatting");
        copy.Click += async (_, _) => { body.Text = Message?.Text ?? ""; await body.Copy(); };
        var header = new Grid { ColumnDefinitions = new("*,Auto") }; header.Children.Add(toggle); Grid.SetColumn(copy, 1); header.Children.Add(copy);
        details = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new StackPanel { Spacing = 6, Children = { header, details } };
    }
    private void Change(Message? old)
    {
        if (old is not null) old.PropertyChanged -= MessageChanged;
        if (Message is not null) Message.PropertyChanged += MessageChanged;
        expanded = !IsOutput; Refresh();
    }
    private void MessageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    public void Collapse() { if (IsOutput) { expanded = false; Refresh(); } }
    private void Refresh()
    {
        body.Muted = IsOutput;
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(IsOutput ? "AppMuted" : "AppAccent"));
        var preview = (Message?.Text ?? "").Split('\n')[0];
        if (preview.Length > 100) preview = preview[..100] + "…";
        title.Text = IsOutput ? (expanded ? "▾ " : "▸ ") + (Message?.Role == "thought" ? "Thinking" : preview.Length > 0 ? preview : "Tool output") : Message?.Label;
        toggle.IsHitTestVisible = IsOutput; toggle.Focusable = IsOutput;
        details.IsVisible = !IsOutput || expanded;
        details.MaxHeight = IsOutput ? 420 : double.PositiveInfinity;
        if (details.IsVisible) body.Text = Message?.Text ?? "";
    }
}
