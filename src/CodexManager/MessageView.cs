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
    private const int ContentChunk = 16 * 1024;
    private const int LargeCommand = 2 * 1024;
    private const int LargeMessage = 32 * 1024;
    private readonly StackPanel largeTextPanel = new() { Spacing = 4 };
    private readonly Grid largeTextHeader = new() { ColumnDefinitions = new("*,Auto") };
    private readonly Button largeTextToggle = new() { Name = "ToggleLargeMessage", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly IconButton copyLargeText = new() { Name = "CopyLargeMessage", Icon = "copy", IconSize = 11, Label = "Copy full message" };
    private readonly SelectableTextBlock largeText = new() { Name = "LargeMessageText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Button moreLargeText = new() { Name = "MoreLargeMessage", Content = "Load more message" };
    private readonly Grid commandHeader = new() { ColumnDefinitions = new("*,Auto") };
    private readonly Button commandToggle = new() { Name = "ToggleCommandContents", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly IconButton copyCommand = new() { Name = "CopyCommandContents", Icon = "copy", IconSize = 11, Label = "Copy full command" };
    private readonly SelectableTextBlock commandText = new() { Name = "CommandContentsText", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(8) };
    private readonly ScrollViewer commandScroll = new() { MaxHeight = 240, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Border commandFrame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    private readonly Button moreCommand = new() { Name = "MoreCommandContents", Content = "Load more command" };
    private readonly Grid outputHeader = new() { ColumnDefinitions = new("*,Auto") };
    private readonly Button outputToggle = new() { Name = "ToggleCommandOutput", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock outputLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, FontSize = 11 };
    private readonly IconButton copyOutput = new() { Name = "CopyCommandOutput", Icon = "copy", IconSize = 11, Label = "Copy command output" };
    private readonly SelectableTextBlock outputText = new() { Name = "CommandOutputText", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(8) };
    private readonly ScrollViewer outputScroll = new() { Name = "CommandOutputScroll", MaxHeight = 240, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly Border outputFrame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    private readonly Button moreOutput = new() { Name = "MoreCommandOutput", Content = "Load more output" };
    private int commandVisible = ContentChunk;
    private int outputVisible = ContentChunk;
    private int largeVisible = ContentChunk;
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
        copyOutput.Click += async (_, _) =>
        {
            if (Message is { Role: "tool" } message && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            { var sections = ToolMessageContent.Locate(message.Text); await clipboard.SetTextAsync(message.Text[sections.OutputStart..]); }
        };
        commandToggle.Click += (_, _) => { if (Message is not null) Message.ToolCommandExpanded = !Message.ToolCommandExpanded; Refresh(); };
        copyCommand.Click += async (_, _) =>
        {
            if (Message is { Role: "tool" } message && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            { var sections = ToolMessageContent.Locate(message.Text); await clipboard.SetTextAsync(message.Text[sections.CommandStart..sections.CommandEnd]); }
        };
        moreCommand.Click += (_, _) => { commandVisible += ContentChunk; Refresh(); };
        moreOutput.Click += (_, _) => { outputVisible += ContentChunk; Refresh(); };
        largeTextToggle.Click += (_, _) => { if (Message is not null) Message.LargeTextExpanded = !Message.LargeTextExpanded; Refresh(); };
        copyLargeText.Click += async (_, _) =>
        {
            if (Message is { } message && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text);
        };
        moreLargeText.Click += (_, _) => { largeVisible += ContentChunk; Refresh(); };
        largeTextHeader.Children.Add(largeTextToggle); Grid.SetColumn(copyLargeText, 1); largeTextHeader.Children.Add(copyLargeText);
        largeText.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        largeTextPanel.Children.Add(largeTextHeader); largeTextPanel.Children.Add(largeText); largeTextPanel.Children.Add(moreLargeText);
        commandHeader.Children.Add(commandToggle); Grid.SetColumn(copyCommand, 1); commandHeader.Children.Add(copyCommand);
        commandText.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        commandScroll.Content = commandText; ScrollViewer.SetIsScrollChainingEnabled(commandScroll, true);
        commandFrame.Child = commandScroll; commandFrame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        outputHeader.Children.Add(outputToggle); Grid.SetColumn(copyOutput, 1); outputHeader.Children.Add(copyOutput);
        outputText.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        outputScroll.Content = outputText; ScrollViewer.SetIsScrollChainingEnabled(outputScroll, true);
        outputFrame.Child = outputScroll; outputFrame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        toolDetails.Children.Add(toolBody); toolDetails.Children.Add(commandHeader); toolDetails.Children.Add(commandFrame); toolDetails.Children.Add(moreCommand);
        toolDetails.Children.Add(outputHeader); toolDetails.Children.Add(outputFrame); toolDetails.Children.Add(moreOutput);
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("AppAccent"));
        frame.Child = new StackPanel { Spacing = 6, Children = { header, details } };
        Content = frame;
    }
    private void Change(Message? old)
    {
        if (old is not null) old.PropertyChanged -= MessageChanged;
        if (attached && Message is not null) Message.PropertyChanged += MessageChanged;
        legacyText = null;
        commandVisible = outputVisible = largeVisible = ContentChunk;
        expanded = !IsOutput || Message?.OutputExpanded == true; Refresh();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; timestampViews.Add(this); timestampTimer.Start(); if (Message is not null) Message.PropertyChanged += MessageChanged; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; timestampViews.Remove(this); if (timestampViews.Count == 0) timestampTimer.Stop(); details.Content = null; toolBody.Content = null; commandText.Text = outputText.Text = largeText.Text = ""; body = null; if (Message is not null) Message.PropertyChanged -= MessageChanged; base.OnDetachedFromVisualTree(e); }
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
            commandVisible = outputVisible = largeVisible = ContentChunk;
            legacySubagent = Message is { Role: "tool", Subagent: null } ? SubagentInfo.FromLegacy(Message.Text) : null;
        }
        var subagent = Message?.Subagent ?? legacySubagent;
        if (subagent is not null)
        {
            toggle.IsVisible = false; history.IsVisible = false; details.IsVisible = true; details.MaxHeight = 600;
            toolBody.Content = null; commandText.Text = outputText.Text = largeText.Text = "";
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
        details.MaxHeight = IsOutput || text.Length > LargeMessage ? 420 : double.PositiveInfinity;
        if (details.IsVisible && attached)
        {
            if (Message?.Role != "tool" && text.Length > LargeMessage)
            {
                toolBody.Content = null; body = null; commandText.Text = outputText.Text = "";
                details.Content = largeTextPanel;
                largeTextToggle.Content = Message?.LargeTextExpanded == true ? "▾ Long message" : "▸ Long message";
                largeText.IsVisible = Message?.LargeTextExpanded == true;
                largeText.Text = largeText.IsVisible ? text[..Math.Min(largeVisible, text.Length)] : "";
                moreLargeText.IsVisible = largeText.IsVisible && largeVisible < text.Length;
                return;
            }
            largeText.Text = "";
            if (body is null || body.SessionNotice != sessionNotice || body.Muted != IsOutput) body = new ChatMarkdown { Muted = IsOutput, SessionNotice = sessionNotice };
            var sections = Message?.Role == "tool" ? ToolMessageContent.Locate(text) : default;
            var largeCommand = Message?.Role == "tool" && sections.HasCommand && sections.CommandEnd - sections.CommandStart > LargeCommand;
            body.Text = legacyResume ? "Chat auto-resumed after unexpected restart" : largeCommand ? text[..(sections.CommandStart - 2)] : Message?.Role == "tool" ? text[..sections.SummaryEnd] : text;
            if (Message?.Role == "tool")
            {
                if (details.Content == body) details.Content = null;
                toolBody.Content = body; details.Content = toolDetails;
                commandHeader.IsVisible = largeCommand;
                commandFrame.IsVisible = largeCommand && Message.ToolCommandExpanded;
                commandToggle.Content = Message.ToolCommandExpanded ? "▾ Command contents" : "▸ Command contents";
                var commandLength = sections.CommandEnd - sections.CommandStart;
                commandText.Text = commandFrame.IsVisible ? text.Substring(sections.CommandStart, Math.Min(commandVisible, commandLength)) : "";
                moreCommand.IsVisible = commandFrame.IsVisible && commandVisible < commandLength;
                var outputLength = text.Length - sections.OutputStart;
                outputHeader.IsVisible = outputLength > 0;
                outputFrame.IsVisible = outputLength > 0 && Message.ToolOutputExpanded;
                outputText.Text = outputFrame.IsVisible ? text.Substring(sections.OutputStart, Math.Min(outputVisible, outputLength)) : "";
                moreOutput.IsVisible = outputFrame.IsVisible && outputVisible < outputLength;
                var first = text.AsSpan(sections.OutputStart, Math.Min(outputLength, 80));
                var lineEnd = first.IndexOf('\n');
                var lineLength = lineEnd < 0 ? first.Length : lineEnd;
                var firstLine = first[..lineLength].Trim().ToString();
                if (outputLength > 80 && lineEnd < 0) firstLine += "…";
                outputLabel.Text = (Message.ToolOutputExpanded ? "▾ Output" : "▸ Output") + (firstLine.Length > 0 ? " · " + firstLine : "");
            }
            else { toolBody.Content = null; details.Content = body; commandHeader.IsVisible = commandFrame.IsVisible = moreCommand.IsVisible = false; outputHeader.IsVisible = outputFrame.IsVisible = moreOutput.IsVisible = false; commandText.Text = outputText.Text = ""; }
        }
        else { details.Content = null; toolBody.Content = null; commandText.Text = outputText.Text = largeText.Text = ""; body = null; }
    }
}
