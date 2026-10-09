using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

// The message composer shared by the desktop chat pane and the mobile/remote view, so both
// get the same layout and every composer fix: slash commands, queued messages, attachment
// chips, the editor, and the session options with attach and send.
public sealed class ChatComposer : Border
{
    public ListBox SlashCommands { get; }
    public Expander QueuePanel { get; }
    public StackPanel QueueItems { get; } = new();
    public TextBlock AttachmentError { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, IsVisible = false };
    public ItemsControl AttachmentList { get; }
    public ComposerEditor Editor { get; }
    public WrapPanel ConfigOptions { get; }
    // On a phone the session options collapse to one tappable line that opens them in a drawer.
    public Button OptionsSummary { get; }
    public TextBlock OptionsSummaryText { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    public IconButton AttachButton { get; }
    public IconButton SpeakButton { get; }
    private CancellationTokenSource? listening;
    public IconButton SendButton { get; }
    public event Action<Attachment>? OpenAttachment;
    public event Action<Attachment>? RemoveAttachment;

    // Names keep their existing values per host; tests and settings look controls up by them.
    public ChatComposer(bool remote, string placeholder)
    {
        Name = remote ? "RemoteComposerBorder" : "ComposerBorder";
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(5); Padding = new Thickness(6); Margin = new Thickness(8, 4, 8, 8);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        this.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
        SlashCommands = new ListBox { Name = remote ? "RemoteSlashCommands" : "SlashCommands", IsVisible = false, FontSize = 11 };
        QueuePanel = new Expander
        {
            Name = remote ? "RemoteQueuePanel" : "QueuePanel", IsVisible = false, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = QueueItems, MaxHeight = 150, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        QueueItems.Name = remote ? "RemoteQueueItems" : "QueueItems";
        AttachmentError.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
        AttachmentError.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) AttachmentError.IsVisible = !string.IsNullOrEmpty(AttachmentError.Text); };
        AttachmentList = new ItemsControl
        {
            Name = remote ? "RemoteAttachmentList" : "AttachmentList",
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<Attachment>((attachment, _) => attachment is null ? null : Chip(attachment))
        };
        Editor = new ComposerEditor { Name = remote ? "RemoteComposer" : "Composer", PlaceholderText = placeholder, MinHeight = 48, MaxHeight = remote ? 140 : 190, Padding = new Thickness(8, 6), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        Editor.Bind(TemplatedControl.FontFamilyProperty, this.GetResourceObservable("ChatFont"));
        Editor.Bind(TemplatedControl.FontSizeProperty, this.GetResourceObservable("ChatFontSize"));
        Editor.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("AppText"));
        ConfigOptions = new WrapPanel { Name = remote ? "RemoteConfigOptions" : "ConfigOptionsPanel", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        OptionsSummary = new Button
        {
            Name = remote ? "RemoteOptionsSummary" : "OptionsSummary", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, MinHeight = 40, Padding = new Thickness(8, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6, Children = { OptionsSummaryText, WithColumn(AppIcons.Create("chevron-up"), 1) } }
        };
        ToolTip.SetTip(OptionsSummary, "Session options");
        OptionsSummaryText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppMuted"));
        AttachButton = new IconButton { Name = remote ? "RemoteAttach" : "AttachButton", Icon = "add", Label = "Attach files" };
        SpeakButton = new IconButton { Name = remote ? "RemoteSpeak" : "SpeakButton", Icon = "mic", Label = "Speak to type", IsVisible = SpeechInput.Available };
        SpeakButton.Click += async (_, _) => await Speak();
        void SpeechChanged() => SpeakButton.IsVisible = SpeechInput.Available || listening is not null;
        AttachedToVisualTree += (_, _) => { SpeechInput.Changed += SpeechChanged; SpeechChanged(); };
        DetachedFromVisualTree += (_, _) => SpeechInput.Changed -= SpeechChanged;
        SendButton = new IconButton { Name = remote ? "RemoteSend" : "SendButton", Icon = "send", Label = "Send", Classes = { "accent" } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Spacing = 2, Children = { SpeakButton, AttachButton, SendButton } };
        Grid.SetColumn(actions, 1);
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new SlashCommandOverlay(Editor, SlashCommands), QueuePanel, AttachmentError, AttachmentList, Editor,
                new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6, Children = { ConfigOptions, OptionsSummary, actions } }
            }
        };
    }
    private static Control WithColumn(Control control, int column) { Grid.SetColumn(control, column); return control; }
    // Dictation goes into the draft at the caret. Recordings stop on a second tap.
    private async Task Speak()
    {
        if (listening is { } active) { active.Cancel(); return; }
        if (SpeechInput.Current is not { } speech) return;
        Editor.TextArea.Focus();
        using var stop = listening = new CancellationTokenSource();
        if (speech.StopsOnTap) { SpeakButton.Icon = "mic-filled"; SpeakButton.Label = "Stop and type what was said"; }
        try
        {
            var spoken = await speech.Listen(stop.Token);
            if (spoken is null) return;
            var caret = Editor.CaretIndex;
            var insertion = SpeechInput.Insertion(Editor.Text, caret, spoken);
            if (insertion.Length == 0) return;
            Editor.Document.Insert(caret, insertion);
            Editor.CaretIndex = caret + insertion.TrimEnd().Length;
            Editor.TextArea.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AttachmentError.Text = AppDiagnostics.Message("Speech input failed", error); }
        finally { listening = null; SpeakButton.Icon = "mic"; SpeakButton.Label = "Speak to type"; SpeakButton.IsVisible = SpeechInput.Available; }
    }
    private Control Chip(Attachment attachment)
    {
        var open = new Button { Name = "OpenAttachmentButton", Tag = attachment, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(open, "Open (edit pasted text in your text editor)");
        var preview = new StackPanel { Spacing = 4 };
        if (attachment.IsImage) preview.Children.Add(new Image { Source = attachment.Thumbnail, Width = 80, Height = 60, Stretch = Stretch.Uniform });
        preview.Children.Add(new TextBlock { Text = attachment.Name, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 });
        open.Content = preview;
        open.Click += (_, _) => OpenAttachment?.Invoke(attachment);
        var remove = new IconButton { Icon = "remove", IconSize = 10, Label = "Remove attachment", Tag = attachment, VerticalAlignment = VerticalAlignment.Top };
        remove.Click += (_, _) => RemoveAttachment?.Invoke(attachment);
        Grid.SetColumn(remove, 1);
        var chip = new Border { Margin = new Thickness(0, 0, 6, 5), Padding = new Thickness(6), CornerRadius = new CornerRadius(4), Child = new Grid { ColumnDefinitions = new("*,Auto"), Children = { open, remove } } };
        chip.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
        return chip;
    }
    // XAML name scopes are sealed after loading, so hosts resolve the parts' names here.
    public Control? Part(string name) => new Control[] { this, SlashCommands, QueuePanel, QueueItems, AttachmentList, Editor, ConfigOptions, OptionsSummary, SpeakButton, AttachButton, SendButton }.FirstOrDefault(c => c.Name == name);
}
