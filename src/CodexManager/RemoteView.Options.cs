using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace CodexManager;

// Phone layout: session options as a one-line summary that opens a bottom drawer, the most recent
// chat opened on launch, and a start or open button instead of an empty chat.
public sealed partial class RemoteView
{
    private readonly StackPanel optionRows = new() { Name = "RemoteOptionRows", Spacing = 12 };
    private Grid? optionsDrawer;
    private Border? optionsSheet;
    private bool? compactOptions;
    private bool recentChatOpened;
    private readonly TextBlock emptyHeading = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Button startChat = new() { Name = "RemoteStartChat", Content = "Start new chat", HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 44, Classes = { "accent" } };
    private readonly Button openWorkspace = new() { Name = "RemoteOpenWorkspace", Content = "Open workspace", HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 44, Classes = { "accent" } };
    private StackPanel? emptyState;
    // Opens a workspace on this host; the button it was asked from anchors the chooser.
    public event Action<Control>? OpenWorkspaceRequested;
    // When set, the first catalog opens the most recent chat instead of leaving the chat empty.
    public bool OpenRecentChat { get; set; }
    private bool CompactOptions => OperatingSystem.IsAndroid() || Bounds.Width is > 0 and < 720;
    private string LastChatKey => "lastRemoteChat:" + host.Address + ":" + host.Port;

    private Control BuildEmptyState()
    {
        startChat.Click += (_, _) => { if (workspaces.SelectedItem is RemoteItem owner) ShowNewChat(startChat, owner.Id); };
        openWorkspace.Click += (_, _) => OpenWorkspaceRequested?.Invoke(openWorkspace);
        emptyState = new StackPanel { Name = "RemoteEmptyState", Spacing = 12, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360, Margin = new Thickness(16), Children = { emptyHeading, startChat, openWorkspace } };
        return emptyState;
    }
    private void UpdateEmptyState()
    {
        if (emptyState is null) return;
        var owner = workspaces.SelectedItem as RemoteItem;
        var empty = chatId is null && catalogJson.Length > 0;
        emptyState.IsVisible = empty; chatComposer.IsVisible = !empty;
        emptyHeading.Text = owner is null ? "No workspaces open on " + host.Name : "No chats yet in " + owner.Name;
        startChat.IsVisible = owner is not null; openWorkspace.IsVisible = owner is null;
    }

    private void RememberChat(string id) => preferences?.Setting(LastChatKey, id + "|" + DateTimeOffset.UtcNow.ToString("O"));
    // The chat last opened on this device, unless the host has worked in another chat since.
    internal string? RecentChat()
    {
        var open = chatRows.OfType<JsonNode>().Where(c => c["archived"]?.GetValue<bool>() != true).ToArray();
        var latest = open.Select(c => (Id: c["id"]!.GetValue<string>(), Updated: DateTimeOffset.TryParse(c["updated"]?.GetValue<string>(), out var updated) ? updated : DateTimeOffset.MinValue))
            .OrderByDescending(c => c.Updated).FirstOrDefault();
        if (preferences?.Setting(LastChatKey)?.Split('|') is [var saved, var at] && open.Any(c => c["id"]?.GetValue<string>() == saved)
            && DateTimeOffset.TryParse(at, out var opened) && (latest.Id is null || opened >= latest.Updated)) return saved;
        return latest.Id;
    }
    private void OpenRecentChatOnce()
    {
        if (!OpenRecentChat || recentChatOpened || requestedWorkspace is not null) return;
        recentChatOpened = true;
        if (chatId is null && RecentChat() is { } recent) SelectChat(recent, userInitiated: false);
    }

    private void UpdateOptionsLayout()
    {
        var compact = CompactOptions;
        if (compactOptions == compact) return;
        compactOptions = compact; configJson = "";
        configs.Children.Clear(); optionRows.Children.Clear();
        chatComposer.OptionsSummary.IsVisible = false; configs.IsVisible = !compact;
        if (!compact) CloseOptions();
    }
    private void RenderConfig(JsonNode result, string id)
    {
        UpdateOptionsLayout();
        var compact = compactOptions == true;
        configs.Children.Clear(); optionRows.Children.Clear();
        var provider = Enum.TryParse<AgentProvider>(result["provider"]?.GetValue<string>(), out var parsed) ? parsed : (AgentProvider?)null;
        var summary = new List<string>();
        foreach (var config in result["config"]!.AsArray())
        {
            var option = new SessionConfig(config!["id"]!.GetValue<string>(), config["name"]!.GetValue<string>(), "select", config["current"]!.GetValue<string>(), config["values"]!.AsArray().Select(v => new SessionValue(v!["value"]!.GetValue<string>(), v["name"]!.GetValue<string>())).ToArray());
            summary.Add(option.Values.FirstOrDefault(v => v.Value == option.Current)?.Name ?? option.Current);
            Task Choose(string value) => Call(new() { ["method"] = "config", ["chatId"] = id, ["configId"] = option.Id, ["value"] = value });
            if (option.Id == VtCodeLaunch.AuthenticationOption)
            {
                var badge = new OptionContent(option) { Margin = new Thickness(4, 2) }; ToolTip.SetTip(badge, option.Name);
                if (compact) optionRows.Children.Add(OptionRow(option.Name, badge)); else configs.Children.Add(badge);
                continue;
            }
            if (provider == AgentProvider.OpenCode && ModelPicker.IsModel(option))
            {
                var picker = new Button { Content = new OptionContent(option, provider), FontSize = compact ? 14 : 11, MinHeight = compact ? 44 : 24, Padding = new Thickness(compact ? 8 : 4), HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
                ToolTip.SetTip(picker, option.Name);
                picker.Flyout = ModelPicker.Create(option, result["recentModels"]?.AsArray().Select(v => v!.GetValue<string>()).ToArray() ?? [], Choose, () => remoteRecentModels, () => Bounds.Width >= 720 && !OperatingSystem.IsAndroid());
                if (compact) optionRows.Children.Add(OptionRow(option.Name, picker)); else configs.Children.Add(picker);
                continue;
            }
            if (compact)
            {
                var box = new ComboBox { Name = "RemoteOption_" + option.Id, ItemsSource = option.Values.Select(v => v.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
                box.SelectedIndex = option.Values.ToList().FindIndex(v => v.Value == option.Current);
                box.SelectionChanged += async (_, _) => { if (box.SelectedIndex >= 0 && option.Values[box.SelectedIndex].Value != option.Current) await Choose(option.Values[box.SelectedIndex].Value); };
                optionRows.Children.Add(OptionRow(option.Name, box));
                continue;
            }
            var button = new Button { Content = new OptionContent(option, provider), FontSize = 11, MinHeight = 24, Padding = new Thickness(4) }; ToolTip.SetTip(button, option.Name);
            button.Click += (_, _) =>
            {
                var menu = new MenuFlyout();
                foreach (var value in option.Values) { var item = new MenuItem { Header = value.Name }; item.Click += async (_, _) => await Choose(value.Value); menu.Items.Add(item); }
                menu.ShowAt(button);
            };
            configs.Children.Add(button);
        }
        chatComposer.OptionsSummaryText.Text = string.Join(" · ", summary);
        chatComposer.OptionsSummary.IsVisible = compact && summary.Count > 0;
        if (summary.Count == 0) CloseOptions();
    }
    private static Control OptionRow(string label, Control field) =>
        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.Medium }, field } };

    private Control BuildOptionsDrawer()
    {
        var scrim = new Border { Name = "RemoteOptionsScrim", Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        // Tapping outside the drawer closes it.
        scrim.PointerPressed += (_, e) => { e.Handled = true; CloseOptions(); };
        var handle = new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        handle.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBorder"));
        var content = new StackPanel { Spacing = 8, Children = { handle, new TextBlock { Text = "Session options", FontSize = 16, FontWeight = FontWeight.SemiBold }, new ScrollViewer { Content = optionRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } };
        optionsSheet = new Border
        {
            Name = "RemoteOptionsSheet", VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(12, 12, 0, 0), Padding = new Thickness(16, 8, 16, 20), Child = content,
            RenderTransform = TransformOperations.Parse("translateY(0px)"),
            Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180) }]
        };
        optionsSheet.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
        optionsDrawer = new Grid { Name = "RemoteOptionsDrawer", IsVisible = false, ZIndex = 30, Children = { scrim, optionsSheet } };
        optionsDrawer.SizeChanged += (_, _) => optionsSheet.MaxHeight = Math.Max(200, optionsDrawer.Bounds.Height * 0.7);
        optionsDrawer.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseOptions(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        chatComposer.OptionsSummary.Click += (_, _) => OpenOptions();
        SizeChanged += (_, _) => UpdateOptionsLayout();
        return optionsDrawer;
    }
    public bool OptionsOpen => optionsDrawer?.IsVisible == true;
    public void OpenOptions()
    {
        if (optionsDrawer is null || optionsSheet is null || optionRows.Children.Count == 0) return;
        optionsDrawer.IsVisible = true;
        // Slide up from below the screen edge.
        var transitions = optionsSheet.Transitions; optionsSheet.Transitions = null;
        optionsSheet.RenderTransform = TransformOperations.Parse("translateY(" + Math.Max(300, Bounds.Height * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px)");
        optionsSheet.Transitions = transitions;
        Dispatcher.UIThread.Post(() => { if (optionsSheet is not null) optionsSheet.RenderTransform = TransformOperations.Parse("translateY(0px)"); }, DispatcherPriority.Render);
    }
    public void CloseOptions() { if (optionsDrawer is not null) optionsDrawer.IsVisible = false; }
}
