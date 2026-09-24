using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    private readonly ContentControl toolBody = new();
    private readonly StackPanel toolDetails = new() { Spacing = 4 };
    private readonly Grid outputHeader = new() { ColumnDefinitions = new("*,Auto") };
    private readonly Button outputToggle = new() { Name = "ToggleCommandOutput", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock outputLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 11 };
    private readonly IconButton copyOutput = new() { Name = "CopyCommandOutput", Icon = "copy", IconSize = 11, Label = "Copy command output" };
    private readonly SelectableTextBlock outputText = new() { Name = "CommandOutputText", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(8) };
    private readonly ScrollViewer outputScroll = new() { Name = "CommandOutputScroll", MaxHeight = 240, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly Border outputFrame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
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
        ScrollViewer.SetIsScrollChainingEnabled(details, true);
        outputToggle.Content = outputLabel;
        outputToggle.Click += (_, _) => { if (Message is not null) Message.ToolOutputExpanded = !Message.ToolOutputExpanded; Refresh(); };
        copyOutput.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(outputText.Text ?? ""); };
        outputHeader.Children.Add(outputToggle); Grid.SetColumn(copyOutput, 1); outputHeader.Children.Add(copyOutput);
        outputText.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        outputScroll.Content = outputText; ScrollViewer.SetIsScrollChainingEnabled(outputScroll, true);
        outputFrame.Child = outputScroll; outputFrame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        toolDetails.Children.Add(toolBody); toolDetails.Children.Add(outputHeader); toolDetails.Children.Add(outputFrame);
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
    { attached = false; timestampViews.Remove(this); if (timestampViews.Count == 0) timestampTimer.Stop(); details.Content = null; toolBody.Content = null; body = null; if (Message is not null) Message.PropertyChanged -= MessageChanged; base.OnDetachedFromVisualTree(e); }
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
            toolBody.Content = null;
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
        var parts = Message?.Role == "tool" ? ToolMessageContent.Split(text) : (Summary: text, Output: "");
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
            body.Text = legacyResume ? "Chat auto-resumed after unexpected restart" : parts.Summary;
            if (Message?.Role == "tool")
            {
                if (details.Content == body) details.Content = null;
                toolBody.Content = body; details.Content = toolDetails;
                outputText.Text = parts.Output;
                outputHeader.IsVisible = parts.Output.Length > 0;
                outputFrame.IsVisible = parts.Output.Length > 0 && Message.ToolOutputExpanded;
                var lineEnd = parts.Output.IndexOf('\n');
                var lineLength = lineEnd < 0 ? parts.Output.Length : lineEnd;
                var firstLine = parts.Output[..Math.Min(lineLength, 80)].Trim();
                if (lineLength > 80) firstLine += "…";
                outputLabel.Text = (Message.ToolOutputExpanded ? "▾ Output" : "▸ Output") + (firstLine.Length > 0 ? " · " + firstLine : "");
            }
            else { toolBody.Content = null; details.Content = body; }
        }
        else { details.Content = null; toolBody.Content = null; outputText.Text = ""; body = null; }
    }
}
