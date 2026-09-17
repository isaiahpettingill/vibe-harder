using Avalonia.Input.Platform;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager;

public sealed partial class RemoteView : UserControl, IDisposable
{
    public Task ShowHistoryActions(Control anchor, Message? message)
    {
        var source = chatId;
        if (source is null) return Task.CompletedTask;
        return HistoryActions.Show(anchor, message, request => { request["chatId"] = source; return Call(request); }, id =>
        {
            if (chatId != source || lifetime.IsCancellationRequested) return;
            chatId = null; SelectChat(id); messageRevisions.Clear(); nextCatalogRefresh = default; _ = RefreshList();
        });
    }
    public async Task OpenFileLink(string target)
    {
        if (chatId is not { } id) throw new IOException("Select a chat first.");
        if (OperatingSystem.IsBrowser())
        {
            var download = await Call(new() { ["method"] = "file/download", ["chatId"] = id, ["path"] = target });
            var url = download?["url"]?.GetValue<string>() ?? throw new IOException("Reconnect and try downloading again.");
            BrowserPlatform.DownloadUrl(url);
            return;
        }
        var path = await FileLinks.Download(Call, id, target, lifetime.Token);
        if (FileLinks.OpenNativeFile is { } open) { await open(path); return; }
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.TryGetFileFromPathAsync(path);
        if (file is null || !await top.Launcher.LaunchFileAsync(file)) throw new IOException("No installed app can open this file.");
    }
    public Control CreateConnectionStatus()
    {
        // Sidebar sections are rebuilt after settings and workspace changes.
        // Each section needs its own visual; the status source is shared.
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, status.GetObservable(TextBlock.TextProperty));
        text.Bind(IsVisibleProperty, status.GetObservable(IsVisibleProperty));
        return text;
    }
    public void RestorePresentation()
    {
        if (lifetime.IsCancellationRequested) return;
        configJson = permissionsJson = queueJson = "";
        configs.Children.Clear(); approvals.Children.Clear(); queuedMessages.Children.Clear();
        output.ItemsSource = null; output.ItemsSource = messages;
        ReconnectHost(); SetPresentationSleeping(false);
    }
    public void ReconnectHost()
    {
        if (lifetime.IsCancellationRequested) return;
        reconnectRequested = true;
        connectAttempt?.Cancel(); connection?.Dispose(); connection = null;
        reconnectAfter = default; reconnectFailures = 0;
        if (!presentationSleeping) timer.Start();
        _ = Connect();
    }
    private readonly WrapPanel attachmentChips = new();
    private readonly TextBlock attachmentError = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly IconButton send = new() { Name = "RemoteSend", Icon = "send", Label = "Send", Classes = { "accent" } };
    private readonly StackPanel queuedMessages = new();
    private readonly Expander queuePanel = new() { Name = "RemoteQueuePanel", IsVisible = false, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool busy, sending, preparing;
    private bool advancingQueue;
    private readonly ListBox slashCommands = new() { Name = "RemoteSlashCommands", IsVisible = false, MaxHeight = 150 };
    private SlashCommand[] availableCommands = [];
    private void UpdateSlashCommands()
    {
        var matches = SlashCommand.Match(availableCommands, composer.Text ?? "");
        var selected = slashCommands.SelectedItem as SlashCommand;
        slashCommands.ItemsSource = matches; slashCommands.SelectedItem = matches.FirstOrDefault(c => c.Name == selected?.Name) ?? matches.FirstOrDefault();
        slashCommands.IsVisible = matches.Count > 0;
    }
    private void InsertSlashCommand()
    {
        if (slashCommands.SelectedItem is not SlashCommand command) return;
        composer.Text = "/" + command.Name + " "; composer.CaretIndex = composer.Text.Length;
        slashCommands.IsVisible = false; composer.Focus();
    }
    private async Task AddFiles(IEnumerable<IStorageItem> files, string? selectedChat)
    {
        foreach (var item in files)
        {
            try
            {
                if (item is not IStorageFile file) throw new IOException("Drop individual files, not folders.");
                var attachment = await AttachmentFiles.Read(file, lifetime.Token);
                if (lifetime.IsCancellationRequested || chatId != selectedChat) return;
                attachments.Add(attachment);
            }
            catch (Exception error) { attachmentError.Text = item.Name + ": " + error.Message; }
        }
        RefreshAttachments();
    }
    private async Task PasteClipboard(bool textOnly)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var selectedChat = chatId; var draft = composer.Text ?? "";
        var start = Math.Min(composer.SelectionStart, composer.SelectionEnd); var end = Math.Max(composer.SelectionStart, composer.SelectionEnd);
        try
        {
            using var data = await clipboard.TryGetDataAsync(); if (data is null) return;
            if (!textOnly)
            {
                if (await AttachmentClipboard.Image(data) is { } image)
                {
                    if (lifetime.IsCancellationRequested || chatId != selectedChat) return;
                    attachments.Add(image); RefreshAttachments(); return;
                }
                var files = (await data.TryGetFilesAsync())?.ToArray() ?? [];
                if (files.Length > 0) { await AddFiles(files, selectedChat); return; }
            }
            var text = await data.TryGetTextAsync();
            if (text is null || lifetime.IsCancellationRequested || chatId != selectedChat) return;
            composer.Text = composer.Text == draft ? draft[..start] + text + draft[end..] : composer.Text + text;
            composer.CaretIndex = composer.Text.Length;
        }
        catch (Exception error) { attachmentError.Text = "Paste failed: " + error.Message; }
    }
    private ListBox output = null!;
    private RemoteTerminalView terminal = null!;
    private string? terminalWorkspace;
    private readonly Dictionary<string, (RemoteTerminalView View, bool Visible, GridLength ChatWidth, GridLength TerminalWidth)> workspaceTerminals = [];
    private Action<string>? switchTerminalWorkspace;
    private bool openingTerminal;
    private Point? swipeStart;
    private string queueJson = "";
    private bool canSteer;
    private string[] remoteRecentModels = [];
    private readonly RemoteHost host;
    public RemoteHost Host => host;
    private RemoteConnection? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox composer = new() { Name = "RemoteComposer", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 56, MaxHeight = 140, PlaceholderText = "Message the agent…" };
    private readonly StackPanel approvals = new();
    private readonly ListBox chats = new();
    private readonly ComboBox workspaces = new();
    private readonly ObservableCollection<Message> messages = [];
    private string? chatId;
    private AgentProvider? messageProvider;
    private string? requestedWorkspace;
    public void SelectWorkspaceId(string id)
    {
        requestedWorkspace = id;
        if (workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == id) is { } selected)
        {
            workspaces.SelectedItem = selected; requestedWorkspace = null; FilterChats(); WorkspaceOpened?.Invoke(id);
        }
    }
    private bool polling;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, string> messageRevisions = [];
    private DateTimeOffset nextCatalogRefresh;
    private string permissionsJson = "";
    private string configJson = "";
    private readonly WrapPanel configs = new() { Orientation = Orientation.Horizontal };
    private readonly List<Attachment> attachments = [];
    private JsonArray chatRows = [];
    private bool presentationSleeping;
    private bool connectionSuspended;
    private bool connectionCollapsed;
    public void SetConnectionCollapsed(bool collapsed)
    {
        connectionCollapsed = collapsed;
        if (collapsed)
        {
            connection?.Dispose(); connection = null;
            status.IsVisible = true; status.Text = "Connection collapsed — expand it in the sidebar to resume.";
            foreach (var permission in notifiedPermissions) PermissionNotifications.Dismiss(host.Address + permission);
            notifiedPermissions.Clear();
        }
        UpdateConnectionActivity(); UpdateSendAction();
    }
    private string catalogJson = "";
    public void SetConnectionSuspended(bool suspended)
    {
        connectionSuspended = suspended;
        UpdateConnectionActivity();
    }
    private void UpdateConnectionActivity()
    {
        if (lifetime.IsCancellationRequested) return;
        if (connectionSuspended || connectionCollapsed) { timer.Stop(); connectAttempt?.Cancel(); terminal.SetSleeping(true); }
        else { reconnectAfter = default; nextCatalogRefresh = default; timer.Interval = TimeSpan.FromMilliseconds(250); timer.Start(); terminal.SetSleeping(presentationSleeping); }
    }
    private bool connecting;
    private bool reconnectRequested;
    private CancellationTokenSource? connectAttempt;
    private DateTimeOffset reconnectAfter;
    private int reconnectFailures;
    private bool hostOffline;
    private System.Collections.IEnumerable? sleepingPage;
    private (object Item, double Within)? sleepingAnchor;
    public void SetPresentationSleeping(bool sleeping)
    {
        if (lifetime.IsCancellationRequested) return;
        if (presentationSleeping != sleeping)
        {
            if (sleeping)
            {
                sleepingAnchor = output.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.CaptureAnchor();
                sleepingPage = output.ItemsSource; output.ItemsSource = null;
            }
            else
            {
                output.ItemsSource = sleepingPage ?? messages; sleepingPage = null;
                var anchor = sleepingAnchor; sleepingAnchor = null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!presentationSleeping && !lifetime.IsCancellationRequested)
                        output.GetVisualDescendants().OfType<TranscriptPanel>().FirstOrDefault()?.RestoreAnchor(anchor);
                }, DispatcherPriority.Loaded);
            }
        }
        presentationSleeping = sleeping; terminal.SetSleeping(sleeping || connectionSuspended || connectionCollapsed);
        if (!connectionSuspended && !connectionCollapsed) { timer.Start(); if (connection is null) { reconnectAfter = default; _ = Connect(); } }
    }
    private bool refreshing;
    private bool viewingHistory;
    private bool activateSelectedChat;
    public event Action<JsonNode>? CatalogChanged;
    public event Action? WorkspaceNavigation;
    public event Action<string>? WorkspaceOpened;
    public string? SelectedChatId => chatId;
    public bool HasWorkspace => workspaces.SelectedItem is RemoteItem;
    public void SelectChat(string id, bool userInitiated = true)
    {
        if (chatRows.Any(c => c?["id"]?.GetValue<string>() == id && c["archived"]?.GetValue<bool>() == true)) return;
        SaveBrowserDraft();
        messageRevisions.Clear(); timer.Interval = TimeSpan.FromMilliseconds(250);
        activateSelectedChat = userInitiated;
        chatSearch.Close(); viewingHistory = false; output.ItemsSource = messages; busy = false; preparing = true; queueJson = ""; queuedMessages.Children.Clear(); availableCommands = []; UpdateSlashCommands(); chatId = id; messageProvider = null; UpdateSendAction(); if (connection is not null) _ = Call(new() { ["method"] = "read", ["chatId"] = id }); messages.Clear(); permissionsJson = ""; configJson = "";
        if (OperatingSystem.IsBrowser())
        {
            restoringBrowserDraft = true;
            try { composer.Text = BrowserPlatform.Read(BrowserDraftKey) ?? ""; }
            finally { restoringBrowserDraft = false; }
        }
        var owner = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == id)?["workspaceId"]?.GetValue<string>();
        if (owner is not null) workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == owner);
        ApplyColors();
    }
    private readonly Func<bool> allowAll;
    private readonly Action? activate;
    private readonly HashSet<string> notifiedPermissions = [];
    private readonly Store? preferences;
    public void ApplyColors()
    {
        if (preferences is null) return;
        var scope = "remote:" + Host.Address + ":" + Host.Port + ":";
        var owner = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == chatId)?["workspaceId"]?.GetValue<string>() ?? (workspaces.SelectedItem as RemoteItem)?.Id;
        if (Content is Panel panel) panel.Background = SidebarColors.Brush(preferences, "workspaceColor:" + scope + owner, true);
        composer.BorderBrush = SidebarColors.Brush(preferences, "chatColor:" + scope + chatId) ?? this.FindResource("AppBorder") as Avalonia.Media.IBrush;
        composer.BorderThickness = new Thickness(SidebarColors.Brush(preferences, "chatColor:" + scope + chatId) is null ? 1 : 2);
    }
    public RemoteView(RemoteHost host, Func<bool>? allowAll = null, Action? activate = null, Store? preferences = null)
    {
        this.preferences = preferences;
        this.allowAll = allowAll ?? (() => false); this.activate = activate;
        this.host = host;
        var panel = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        var connectionNotice = new Grid { Name = "RemoteConnectionNotice", ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        connectionNotice.Bind(IsVisibleProperty, status.GetObservable(IsVisibleProperty));
        var connectionText = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        connectionText.Bind(TextBlock.TextProperty, status.GetObservable(TextBlock.TextProperty)); connectionNotice.Children.Add(connectionText);
        var retry = new IconButton { Icon = "refresh", Label = "Retry connection" }; retry.Click += (_, _) => ReconnectHost(); Grid.SetColumn(retry, 1); connectionNotice.Children.Add(retry);
        var remoteHeader = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        remoteHeader.Children.Add(new StackPanel { Children = { new TextBlock { Text = host.Name, VerticalAlignment = VerticalAlignment.Center }, connectionNotice } });
        var copyChat = new IconButton { Name = "RemoteCopyChat", Icon = "copy", Label = "Copy whole chat" };
        copyChat.Click += async (_, _) =>
        {
            if (chatId is not { } id || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            copyChat.IsEnabled = false;
            try
            {
                var text = new System.Text.StringBuilder();
                var after = -1;
                while (!lifetime.IsCancellationRequested)
                {
                    var page = await Call(new JsonObject { ["method"] = "chat/export", ["chatId"] = id, ["after"] = after });
                    if (page is null) throw new IOException("Could not retrieve the complete chat. Reconnect and try again.");
                    foreach (var item in page["messages"]!.AsArray()) text.Append(item!["label"]!.GetValue<string>()).Append('\n').Append(item["text"]!.GetValue<string>()).Append("\n\n");
                    if (page["after"] is null) break;
                    var next = page["after"]!.GetValue<int>();
                    if (next <= after) throw new IOException("The host returned a repeated chat page.");
                    after = next;
                }
                lifetime.Token.ThrowIfCancellationRequested();
                await clipboard.SetTextAsync(text.ToString());
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { status.IsVisible = true; status.Text = "Could not copy chat: " + error.Message; }
            finally { copyChat.IsEnabled = true; }
        };
        Grid.SetColumn(copyChat, 3); remoteHeader.Children.Add(copyChat);
        var searchChat = new IconButton { Icon = "search", Label = "Search in chat (Ctrl+F)" };
        searchChat.Click += (_, _) => OpenChatSearch();
        remoteHeader.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); Grid.SetColumn(searchChat, remoteHeader.ColumnDefinitions.Count - 1); remoteHeader.Children.Add(searchChat);
        var forkChat = new IconButton { Name = "RemoteForkChat", Icon = "fork", Label = "Fork chat" };
        forkChat.Click += async (_, _) => await ShowHistoryActions(forkChat, null); Grid.SetColumn(forkChat, 1); remoteHeader.Children.Add(forkChat);
        var openTerminal = new IconButton { Name = "RemoteOpenTerminal", Icon = "terminal", Label = "Open remote terminal" };
        openTerminal.Click += async (_, _) => await ShowTerminal(); Grid.SetColumn(openTerminal, 2); remoteHeader.Children.Add(openTerminal); panel.Children.Add(remoteHeader);
        openTerminal.IsEnabled = workspaces.SelectedItem is not null;
        workspaces.SelectionChanged += (_, _) => { openTerminal.IsEnabled = workspaces.SelectedItem is not null; if (workspaces.SelectedItem is RemoteItem owner) switchTerminalWorkspace?.Invoke(owner.Id); ApplyColors(); };
        workspaces.SelectionChanged += (_, _) => FilterChats();
        var split = new Grid { ColumnDefinitions = new("0,0,*"), RowDefinitions = new("Auto,*") }; Grid.SetRow(split, 1); panel.Children.Add(split);
        chats.SelectionChanged += (_, _) => { if (!refreshing && chats.SelectedItem is RemoteItem selected && chatId != selected.Id) SelectChat(selected.Id); };
        chats.IsVisible = false; split.Children.Add(chats); var divider = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false }; Grid.SetColumn(divider, 1); split.Children.Add(divider);
        output = new ListBox { ItemsSource = messages, ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()), Background = Avalonia.Media.Brushes.Transparent, ItemTemplate = new FuncDataTemplate<Message>((message, _) => { var view = new MessageView { Margin = new Thickness(8) }; view.DataContextChanged += (_, _) => view.Message = view.DataContext as Message; return view; }, true) };
        output.ItemContainerTheme = (Avalonia.Styling.ControlTheme)Application.Current!.Resources["TranscriptItemTheme"]!;
        ScrollViewer.SetVerticalScrollBarVisibility(output, OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
        ScrollViewer.SetAllowAutoHide(output, false);
        Grid.SetColumn(chatSearch, 2); split.Children.Add(chatSearch); InitializeChatSearch();
        Grid.SetColumn(output, 2); Grid.SetRow(output, 1); split.Children.Add(output);
        var latest = new IconButton { Name = "RemoteLatest", Icon = "chevron-down", Label = "Return to latest message", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 8), IsVisible = false };
        latest.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface")); Grid.SetColumn(latest, 2); Grid.SetRow(latest, 1); split.Children.Add(latest);
        var navigation = transcriptNavigation = new TranscriptNavigation(output, latest, () => viewingHistory, async newer =>
        {
            if (chatId is null || output.Items.Count == 0) return;
            var visible = output.Items.OfType<Message>().ToArray(); var id = chatId;
            var result = await Call(new() { ["method"] = "chat", ["chatId"] = id, [newer ? "after" : "before"] = newer ? visible[^1].Sequence : visible[0].Sequence });
            if (result is null || id != chatId) return;
            var page = result["messages"]!.AsArray().Select(row => ReadMessage(row!)).ToArray();
            if (page.Length == 0) return;
            var merged = visible.Concat(page).GroupBy(m => m.Id).Select(g => g.First()).OrderBy(m => m.Sequence);
            viewingHistory = true;
            TranscriptNavigation.ReplacePage(output, merged.ToArray());
            UpdateSendAction();
        });
        latest.Click += (_, _) => { viewingHistory = false; output.ItemsSource = messages; UpdateSendAction(); if (messages.Count > 0) output.ScrollIntoView(messages[^1]); navigation.Update(); };
        approvals.Children.CollectionChanged += (_, _) => UpdateSendAction();
        var approvalScroll = new ScrollViewer { Content = approvals, MaxHeight = 180 };
        panel.SizeChanged += (_, _) => approvalScroll.MaxHeight = Math.Clamp(panel.Bounds.Height * .35, 64, 220);
        Grid.SetRow(approvalScroll, 2); panel.Children.Add(approvalScroll);
        queuePanel.Content = new ScrollViewer { Content = queuedMessages, MaxHeight = 150, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        queuedMessages.Children.CollectionChanged += (_, _) => { var count = queuedMessages.Children.Count; queuePanel.IsVisible = count > 0; queuePanel.Header = $"{count} queued message{(count == 1 ? "" : "s")}"; };
        var input = new StackPanel { Spacing = 6 }; Grid.SetRow(input, 3); panel.Children.Add(input); input.Children.Add(queuePanel); input.Children.Add(attachmentError); input.Children.Add(attachmentChips); input.Children.Add(new SlashCommandOverlay(composer, slashCommands)); input.Children.Add(composer);
        slashCommands.PointerReleased += (_, _) => InsertSlashCommand();
        DragDrop.SetAllowDrop(input, true);
        input.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }, RoutingStrategies.Bubble, true);
        input.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true; var selectedChat = chatId;
            try
            {
                var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
                if (files.Length > 0) await AddFiles(files, selectedChat);
                else if (AttachmentClipboard.Image(e.DataTransfer) is { } image) { attachments.Add(image); RefreshAttachments(); }
            }
            catch (Exception error) { attachmentError.Text = "Drop failed: " + error.Message; }
        }, RoutingStrategies.Bubble, true);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 }; input.Children.Add(footer); footer.Children.Add(configs);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Spacing = 4 }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var attach = new IconButton { Name = "RemoteAttach", Icon = "add", Label = "Attach images or files" };
        attach.Click += async (_, _) =>
        {
            var selectedChat = chatId;
            attach.IsEnabled = false; attachmentError.Text = "";
            try
            {
                var provider = TopLevel.GetTopLevel(this)!.StorageProvider;
                IReadOnlyList<IStorageFile> selected;
                using (MobileAppSecurity.BeginFilePicker())
                    selected = await provider.OpenFilePickerAsync(new() { Title = "Attach files", AllowMultiple = true, FileTypeFilter = [FilePickerFileTypes.All] });
                try { if (!lifetime.IsCancellationRequested && chatId == selectedChat) await AddFiles(selected, selectedChat); }
                finally { foreach (var file in selected) file.Dispose(); }
            }
            catch (Exception error) { attachmentError.Text = "Could not attach files: " + error.Message; }
            finally { attach.IsEnabled = true; }
        }; actions.Children.Add(attach);
        actions.Children.Add(send);
        composer.TextChanged += (_, _) => { UpdateSendAction(); UpdateSlashCommands(); SaveBrowserDraft(); };
        send.Click += async (_, _) => await SendOrStop();
        composer.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await PasteClipboard(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); return; }
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { e.Handled = true; composer.SelectedText = "\n"; return; }
            if (slashCommands.IsVisible && e.KeyModifiers == KeyModifiers.None)
            {
                if (e.Key is Key.Enter or Key.Tab) { e.Handled = true; InsertSlashCommand(); return; }
                if (e.Key is Key.Up or Key.Down) { e.Handled = true; slashCommands.SelectedIndex = Math.Clamp(slashCommands.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, slashCommands.ItemCount - 1); slashCommands.ScrollIntoView(slashCommands.SelectedItem!); return; }
                if (e.Key == Key.Escape) { e.Handled = true; slashCommands.IsVisible = false; return; }
            }
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && busy)
            { e.Handled = true; await SendOrStop("send-now"); return; }
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            if (HasDraft) await SendOrStop("send");
            else if (!advancingQueue && chatId is { } id && connection is not null)
            {
                advancingQueue = true;
                try { await Call(new() { ["method"] = "queue/advance", ["chatId"] = id }); }
                finally { advancingQueue = false; }
            }
        }, RoutingStrategies.Tunnel);
        UpdateSendAction();
        terminal = new RemoteTerminalView(Call) { IsVisible = false, ZIndex = 20 };
        terminal.Bind(Panel.BackgroundProperty, this.GetResourceObservable("AppBackground"));
        var terminalLayout = new Grid { Name = "RemoteTerminalLayout", ColumnDefinitions = new ColumnDefinitions("*,0,0") };
        var terminalSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
        Grid.SetColumn(terminalSplitter, 1); terminalLayout.Children.Add(panel); terminalLayout.Children.Add(terminalSplitter); terminalLayout.Children.Add(terminal);
        bool? docked = null;
        void LayoutTerminal()
        {
            // Use the whole window width: a laptop's sidebar must not make its chat look like a phone.
            var wide = (TopLevel.GetTopLevel(this)?.Bounds.Width ?? Bounds.Width) >= 720;
            var dock = wide && terminal.IsVisible;
            if (docked == dock) return;
            docked = dock;
            terminalLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            terminalLayout.ColumnDefinitions[1].Width = new GridLength(dock ? 5 : 0);
            terminalLayout.ColumnDefinitions[2].Width = dock ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            terminalSplitter.IsVisible = dock;
            Grid.SetColumn(terminal, dock ? 2 : 0);
        }
        SizeChanged += (_, _) => LayoutTerminal();
        TopLevel? layoutTopLevel = null;
        void WindowResized(object? sender, SizeChangedEventArgs e) => LayoutTerminal();
        AttachedToVisualTree += (_, _) => { layoutTopLevel = TopLevel.GetTopLevel(this); if (layoutTopLevel is not null) layoutTopLevel.SizeChanged += WindowResized; LayoutTerminal(); };
        DetachedFromVisualTree += (_, _) => { if (layoutTopLevel is not null) layoutTopLevel.SizeChanged -= WindowResized; layoutTopLevel = null; };
        terminal.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) LayoutTerminal(); };
        terminal.Back += () => terminal.SetVisible(false);
        switchTerminalWorkspace = id =>
        {
            if (terminalWorkspace == id) return;
            if (terminalWorkspace is { } previous)
                workspaceTerminals[previous] = (terminal, terminal.IsVisible, terminalLayout.ColumnDefinitions[0].Width, terminalLayout.ColumnDefinitions[2].Width);
            else terminal.Dispose();
            terminal.SetVisible(false); terminalLayout.Children.Remove(terminal);
            terminalWorkspace = id;
            if (workspaceTerminals.TryGetValue(id, out var saved)) terminal = saved.View;
            else
            {
                var view = new RemoteTerminalView(Call) { IsVisible = false, ZIndex = 20 };
                view.Bind(Panel.BackgroundProperty, this.GetResourceObservable("AppBackground"));
                view.Back += () => view.SetVisible(false);
                view.PropertyChanged += (_, e) => { if (ReferenceEquals(terminal, view) && e.Property == IsVisibleProperty) LayoutTerminal(); };
                terminal = view;
            }
            terminalLayout.Children.Add(terminal);
            terminal.SetSleeping(presentationSleeping || connectionSuspended || connectionCollapsed);
            docked = null; terminal.SetVisible(saved.Visible); LayoutTerminal();
            if (docked == true && saved.Visible && saved.TerminalWidth.Value > 0) { terminalLayout.ColumnDefinitions[0].Width = saved.ChatWidth; terminalLayout.ColumnDefinitions[2].Width = saved.TerminalWidth; }
        };
        AddHandler(PointerPressedEvent, (_, e) => { if (e.Pointer.Type == PointerType.Touch && (!terminal.IsVisible || e.GetPosition(terminal).Y < 52)) swipeStart = e.GetPosition(this); else swipeStart = null; }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch || swipeStart is not { } start) return;
            swipeStart = null; var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < 80 || Math.Abs(delta.X) < Math.Abs(delta.Y) * 1.5) return;
            if (delta.X > 0 && !terminal.IsVisible) await ShowTerminal();
            else if (delta.X < 0 && terminal.IsVisible) terminal.SetVisible(false);
        }, RoutingStrategies.Tunnel, true);
        Content = terminalLayout;
        timer.Tick += async (_, _) =>
        {
            if (connectionSuspended || connectionCollapsed || polling) return;
            if (connection is null) { if (!connecting && DateTimeOffset.UtcNow >= reconnectAfter) await Connect(); return; }
            if (presentationSleeping || chatId is null)
            {
                if (DateTimeOffset.UtcNow < nextCatalogRefresh) return;
                nextCatalogRefresh = DateTimeOffset.UtcNow.AddSeconds(2);
                polling = true;
                try { await RefreshList(); }
                catch (Exception error) { AppDiagnostics.Record("Remote catalog refresh", error); }
                finally { polling = false; }
                return;
            }
            polling = true; var id = chatId;
            try
            {
                var known = new JsonObject();
                foreach (var message in messages) if (messageRevisions.TryGetValue(message.Id, out var revision)) known[message.Id] = revision;
                var result = await Call(new() { ["method"] = "chat", ["chatId"] = id, ["activate"] = activateSelectedChat, ["knownMessages"] = known }); if (presentationSleeping || result is null || id != chatId) return;
                activateSelectedChat = false;
                messageProvider = Enum.TryParse<AgentProvider>(result["provider"]?.GetValue<string>(), out var provider) && Enum.IsDefined(provider) ? provider : null;
                status.Text = result["status"]?.GetValue<string>() + " · " + result["queued"] + " queued";
                status.IsVisible = false;
                busy = result["busy"]?.GetValue<bool>() == true; preparing = result["preparing"]?.GetValue<bool>() ?? (busy && (result["status"]?.GetValue<string>() is { } state && (state.StartsWith("Loading") || state.StartsWith("Connecting") || state.StartsWith("Reconnecting")))); UpdateSendAction();
                UpdateRemoteQueue(result);
                timer.Interval = TimeSpan.FromMilliseconds(busy || preparing ? 250 : 2000);
                var commands = result["commandOptions"] is JsonArray detailed
                    ? detailed.Select(c => new SlashCommand(c!["name"]!.GetValue<string>(), c["description"]?.GetValue<string>() ?? "", c["hint"]?.GetValue<string>())).ToArray()
                    : result["commands"]?.AsArray().Select(c => new SlashCommand(c!.GetValue<string>().TrimStart('/'), "", null)).ToArray() ?? [];
                if (!commands.SequenceEqual(availableCommands)) { availableCommands = commands; UpdateSlashCommands(); }
                navigation.Update();
                remoteRecentModels = result["recentModels"]?.AsArray().Select(v => v!.GetValue<string>()).ToArray() ?? [];
                var configText = result["config"]!.ToJsonString();
                if (configText != configJson)
                {
                    configJson = configText; configs.Children.Clear();
                    foreach (var config in result["config"]!.AsArray())
                    {
                        var option = new SessionConfig(config!["id"]!.GetValue<string>(), config["name"]!.GetValue<string>(), "select", config["current"]!.GetValue<string>(), config["values"]!.AsArray().Select(v => new SessionValue(v!["value"]!.GetValue<string>(), v["name"]!.GetValue<string>())).ToArray());
                        if (option.Id == VtCodeLaunch.AuthenticationOption) { var badge = new OptionContent(option) { Margin = new Thickness(4, 2) }; ToolTip.SetTip(badge, option.Name); configs.Children.Add(badge); continue; }
                        var button = new Button { Content = new OptionContent(option, Enum.TryParse<AgentProvider>(result["provider"]?.GetValue<string>(), out var optionProvider) ? optionProvider : null), FontSize = 11, MinHeight = OperatingSystem.IsAndroid() ? 40 : 24, Padding = new Thickness(4) }; ToolTip.SetTip(button, config["name"]!.GetValue<string>());
                        if (result["provider"]?.GetValue<string>() == "OpenCode" && ModelPicker.IsModel(option))
                        {
                            var selectedChat = id;
                            button.Flyout = ModelPicker.Create(option, result["recentModels"]?.AsArray().Select(v => v!.GetValue<string>()).ToArray() ?? [],
                                async value => await Call(new() { ["method"] = "config", ["chatId"] = selectedChat, ["configId"] = option.Id, ["value"] = value }), () => remoteRecentModels);
                            configs.Children.Add(button); continue;
                        }
                        button.Click += (_, _) => { var menu = new MenuFlyout(); foreach (var value in config["values"]!.AsArray()) { var item = new MenuItem { Header = value!["name"]!.GetValue<string>() }; item.Click += async (_, _) => await Call(new() { ["method"] = "config", ["chatId"] = chatId, ["configId"] = config["id"]!.DeepClone(), ["value"] = value["value"]!.DeepClone() }); menu.Items.Add(item); } menu.ShowAt(button); }; configs.Children.Add(button);
                    }
                }
                var firstPage = messages.Count == 0;
                var follow = firstPage || output.ItemsPanelRoot is not TranscriptPanel panel || panel.IsFollowingEnd;
                ApplyMessages(result["messages"]!.AsArray());
                if (DateTimeOffset.UtcNow >= nextCatalogRefresh) { nextCatalogRefresh = DateTimeOffset.UtcNow.AddSeconds(2); await RefreshList(); }
                if (follow && !viewingHistory && messages.Count > 0) Dispatcher.UIThread.Post(() =>
                {
                    if (!lifetime.IsCancellationRequested && !presentationSleeping && !viewingHistory && id == chatId && messages.Count > 0 && (firstPage || output.ItemsPanelRoot is TranscriptPanel { IsFollowingEnd: true })) output.ScrollIntoView(messages[^1]);
                });
                if (lifetime.IsCancellationRequested || id != chatId) return;
                var permissionText = result["permissions"]!.ToJsonString();
                if (permissionsJson != permissionText)
                {
                    permissionsJson = permissionText; approvals.Children.Clear();
                    foreach (var permission in result["permissions"]!.AsArray())
                    {
                        var permissionId = permission!["id"]!.GetValue<string>();
                        approvals.Children.Add(new PermissionCard(permission.AsObject(), async option =>
                        {
                            if (await Call(new() { ["method"] = "approve", ["permissionId"] = permissionId, ["optionId"] = option }) is null) throw new IOException("Reconnect and try again.");
                        }));
                    }
                }
            }
            catch (Exception error)
            {
                AppDiagnostics.Record("Remote chat refresh", error);
                if (!lifetime.IsCancellationRequested) { status.IsVisible = true; status.Text = "Could not refresh chat: " + error.Message; }
            }
            finally { polling = false; }
        };
        timer.Start();
        AttachedToVisualTree += async (_, _) => await Connect();
    }
    public async Task ShowTerminal()
    {
        if (openingTerminal || workspaces.SelectedItem is not RemoteItem owner) return;
        switchTerminalWorkspace?.Invoke(owner.Id);
        var selectedTerminal = terminal;
        openingTerminal = true; selectedTerminal.SetVisible(true);
        try
        {
            if (selectedTerminal.TerminalId is null)
            {
                await selectedTerminal.Open(owner.Id, host.Name + " · " + owner.Name);
            }
        }
        finally { openingTerminal = false; }
    }
    private bool HasDraft => !string.IsNullOrWhiteSpace(composer.Text) || attachments.Count > 0;
    private bool restoringBrowserDraft;
    private string BrowserDraftKey => "draft:" + host.Address + ":" + host.Port + ":" + chatId;
    private void SaveBrowserDraft()
    {
        if (!OperatingSystem.IsBrowser() || chatId is null || restoringBrowserDraft) return;
        try { BrowserPlatform.Write(BrowserDraftKey, composer.Text ?? ""); }
        catch (Exception error) { attachmentError.Text = "Could not save draft: " + error.Message; }
    }
    private void UpdateSendAction()
    {
        TranscriptPanel.SetShowProgress(output, !viewingHistory && busy && !preparing && connection is not null && chatId is not null && approvals.Children.Count == 0);
        var stop = busy && !preparing && !HasDraft;
        send.Icon = preparing ? "connecting" : stop ? "stop" : "send";
        send.Label = preparing ? "Loading chat" : stop ? "Stop" : busy ? "Queue message" : "Send";
        send.IsEnabled = connection is not null && !sending && !preparing && chatId is not null && (stop || HasDraft);
    }
    private async Task SendOrStop(string method = "send")
    {
        if (sending || preparing || chatId is null) return;
        var id = chatId; var text = composer.Text ?? ""; var sent = attachments.ToArray();
        var stop = busy && !preparing && !HasDraft;
        if (!stop && !HasDraft) return;
        sending = true; timer.Interval = TimeSpan.FromMilliseconds(250); UpdateSendAction();
        try
        {
            var result = await Call(new() { ["method"] = stop ? method == "send-now" ? "queue/interrupt" : "stop" : method, ["chatId"] = id, ["text"] = text, ["attachments"] = JsonSerializer.SerializeToNode(sent, StoreJsonContext.Default.AttachmentArray) });
            if (result is not null && id == chatId)
            {
                if (method == "steer" && !stop)
                {
                    if (result.GetValue<bool>()) { if (composer.Text == text) composer.Text = ""; foreach (var file in sent) attachments.Remove(file); RefreshAttachments(); }
                    return;
                }
                busy = result["busy"]?.GetValue<bool>() == true; preparing = result["preparing"]?.GetValue<bool>() ?? (busy && (result["status"]?.GetValue<string>() is { } state && (state.StartsWith("Loading") || state.StartsWith("Connecting") || state.StartsWith("Reconnecting"))));
                if (!stop) { if (composer.Text == text) composer.Text = ""; foreach (var file in sent) attachments.Remove(file); RefreshAttachments(); }
            }
        }
        finally { sending = false; UpdateSendAction(); }
    }
    private void UpdateRemoteQueue(JsonNode result)
    {
        var queue = result["queue"]?.AsArray() ?? [];
        var supported = result["canSteer"]?.GetValue<bool>() == true;
        var json = queue.ToJsonString();
        if (json == queueJson && supported == canSteer) return;
        queueJson = json; canSteer = supported; queuedMessages.Children.Clear();
        foreach (var item in queue)
        {
            var id = item!["id"]!.GetValue<string>(); var chat = chatId;
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(2) };
            row.Children.Add(new TextBlock { Text = item["text"]?.GetValue<string>() is { Length: > 0 } text ? text : item["attachments"] + " attachments", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxLines = 2, FontSize = 11 });
            var steer = new IconButton { Name = "RemoteSteerQueued", Icon = "steer", Label = "Steer with queued message", IsVisible = supported };
            var remove = new IconButton { Icon = "remove", Label = "Remove queued message" };
            var edit = new IconButton { Icon = "edit", Label = "Edit queued message" };
            var sendNow = new IconButton { Icon = "send", Label = "Send now (interrupt current turn)" };
            edit.Click += (_, _) =>
            {
                var popup = new Flyout(); var input = new TextBox { Text = item["text"]?.GetValue<string>() ?? "", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinWidth = 240, MaxWidth = 420, MaxHeight = 200 };
                var save = new Button { Content = "Save" };
                save.Click += async (_, _) => { save.IsEnabled = false; try { if (await Call(new() { ["method"] = "queue/edit", ["chatId"] = chat, ["queueId"] = id, ["text"] = input.Text ?? "" }) is not null) popup.Hide(); } finally { save.IsEnabled = true; } };
                popup.Content = new StackPanel { Spacing = 8, Children = { input, save } }; popup.ShowAt(edit); input.Focus();
            };
            sendNow.Click += async (_, _) => { sendNow.IsEnabled = false; try { await Call(new() { ["method"] = "queue/send", ["chatId"] = chat, ["queueId"] = id }); } finally { sendNow.IsEnabled = true; } };
            steer.Click += async (_, _) => { steer.IsEnabled = false; await Call(new() { ["method"] = "queue/steer", ["chatId"] = chat, ["queueId"] = id }); steer.IsEnabled = true; };
            remove.Click += async (_, _) => await Call(new() { ["method"] = "queue/remove", ["chatId"] = chat, ["queueId"] = id });
            Grid.SetColumn(remove, 1); row.Children.Add(remove); Grid.SetColumn(edit, 2); row.Children.Add(edit); Grid.SetColumn(steer, 3); row.Children.Add(steer); Grid.SetColumn(sendNow, 4); row.Children.Add(sendNow); queuedMessages.Children.Add(row);
        }
    }
    private Message ReadMessage(JsonNode row)
    {
        var message = new Message { Provider = messageProvider, Id = row["id"]!.GetValue<string>(), Role = row["role"]!.GetValue<string>(), Sequence = row["sequence"]?.GetValue<int>() ?? 0, Text = row["text"]!.GetValue<string>() };
        message.Subagent = row["subagent"]?.Deserialize(StoreJsonContext.Default.SubagentInfo);
        foreach (var file in row["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? []) message.Attachments.Add(file);
        return message;
    }
    private void ApplyMessages(JsonArray rows)
    {
        if (presentationSleeping) return;
        Dictionary<string, Message>? byId = null;
        foreach (var row in rows)
        {
            var id = row!["id"]!.GetValue<string>();
            if (row["revision"] is { } revision) messageRevisions[id] = revision.GetValue<string>();
            if (row["text"] is null) continue;
            byId ??= messages.ToDictionary(m => m.Id);
            if (!byId.TryGetValue(id, out var message)) { message = new Message { Provider = messageProvider, Id = id, Role = row["role"]!.GetValue<string>(), Sequence = row["sequence"]?.GetValue<int>() ?? 0 }; messages.Add(message); byId.Add(id, message); }
            message.Text = row["text"]!.GetValue<string>();
            message.Subagent = row["subagent"]?.Deserialize(StoreJsonContext.Default.SubagentInfo);
            if (row["attachments"] is { } files)
            {
                var incoming = files.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? [];
                if (!message.Attachments.SequenceEqual(incoming)) { message.Attachments.Clear(); foreach (var file in incoming) message.Attachments.Add(file); }
            }
        }
        while (messages.Count > Chat.HistoryPageSize) messages.RemoveAt(0);
        var retained = messages.Select(m => m.Id).ToHashSet();
        foreach (var id in messageRevisions.Keys.Where(id => !retained.Contains(id)).ToArray()) messageRevisions.Remove(id);
    }
    private async Task Connect()
    {
        if (connecting || connectionSuspended || connectionCollapsed || lifetime.IsCancellationRequested) return;
        reconnectRequested = false;
        connecting = true;
        connection?.Dispose(); connection = null;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connectAttempt = attempt; attempt.CancelAfter(TimeSpan.FromSeconds(30));
        status.IsVisible = true; status.Text = hostOffline ? "Offline · checking connection…" : "Connecting…";
        UpdateSendAction();
        RemoteConnection? candidate = null;
        try { candidate = new RemoteConnection(host); await candidate.Connect(attempt.Token); attempt.Token.ThrowIfCancellationRequested(); connection = candidate; hostOffline = false; status.Text = "Connected"; status.IsVisible = false; await RefreshList(); if (connection is not null) reconnectFailures = 0; }
        catch (Exception error)
        {
            if (!connectionCollapsed && !connectionSuspended && !lifetime.IsCancellationRequested && !attempt.IsCancellationRequested)
            {
                hostOffline = candidate?.TransportConnected == false;
                status.IsVisible = true;
                status.Text = hostOffline ? $"Offline · cannot reach {host.Address}:{host.Port}. Retrying automatically."
                    : error.Message + (candidate?.ObservedFingerprint is { } pin && pin != host.Fingerprint ? "\nObserved host fingerprint: " + pin + "\nVerify it on the host before changing the saved fingerprint." : "\nRetrying connection…");
            }
            candidate?.Dispose(); if (ReferenceEquals(connection, candidate)) connection = null;
        }
        finally
        {
            connectAttempt = null; connecting = false;
            if (connection is null) reconnectAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(reconnectFailures++, 5))));
            if (reconnectRequested && !connectionSuspended && !connectionCollapsed && !lifetime.IsCancellationRequested) _ = Connect();
        }
    }
    private IReadOnlyList<AgentOption> enabledProviders = AgentProviders.All.Where(p => !AgentProviders.IsAdditional(p.Provider)).ToArray();
    private async Task RefreshList()
    {
        var result = await Call(new() { ["method"] = "list" }); if (result is null || lifetime.IsCancellationRequested) return;
        enabledProviders = AgentProviders.All.Where(p => result["providers"] is JsonArray providers ? providers.Any(v => v?.GetValue<string>() == p.Provider.ToString()) : !AgentProviders.IsAdditional(p.Provider)).ToArray();
        chatRows = result["chats"]!.AsArray();
        if (result["permissions"] is JsonArray pending)
        {
            foreach (var permission in pending.OfType<JsonObject>())
            {
                var permissionId = permission["id"]!.GetValue<string>();
                using var parsed = System.Text.Json.JsonDocument.Parse(permission.ToJsonString());
                if (allowAll() && PermissionPolicy.AllowedOption(parsed.RootElement) is { } allowed)
                {
                    if (await Call(new() { ["method"] = "approve", ["permissionId"] = permissionId, ["optionId"] = allowed }) is not null) continue;
                }
                var permissionChat = permission["chatId"]!.GetValue<string>();
                if (notifiedPermissions.Add(permissionId)) PermissionNotifications.Show(host.Address + permissionId, permission["chatTitle"]?.GetValue<string>() ?? "Remote chat", () => { activate?.Invoke(); SelectChat(permissionChat); });
            }
            var activePermissions = pending.Select(p => p!["id"]!.GetValue<string>()).ToHashSet();
            foreach (var resolved in notifiedPermissions.Except(activePermissions)) PermissionNotifications.Dismiss(host.Address + resolved);
            notifiedPermissions.IntersectWith(activePermissions);
        }
        var requested = requestedWorkspace;
        var json = result.ToJsonString();
        if (catalogJson == json && requested is null) return;
        var selected = requested ?? chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == chatId)?["workspaceId"]?.GetValue<string>() ?? (workspaces.SelectedItem as RemoteItem)?.Id;
        selected ??= chatRows.FirstOrDefault(c => c?["archived"]?.GetValue<bool>() != true)?["workspaceId"]?.GetValue<string>();
        refreshing = true;
        workspaces.ItemsSource = result["workspaces"]!.AsArray().Select(w => new RemoteItem(w!["id"]!.GetValue<string>(), w["name"]!.GetValue<string>())).ToArray();
        workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == selected) ?? workspaces.Items.OfType<RemoteItem>().FirstOrDefault();
        refreshing = false;
        FilterChats();
        if (catalogJson != json) { catalogJson = json; CatalogChanged?.Invoke(result.DeepClone()); }
        if (requested is not null && (workspaces.SelectedItem as RemoteItem)?.Id == requested) { requestedWorkspace = null; WorkspaceOpened?.Invoke(requested); }
    }
    private void FilterChats()
    {
        if (refreshing) return;
        var owner = (workspaces.SelectedItem as RemoteItem)?.Id;
        var selectedId = chatId;
        refreshing = true;
        try
        {
            chats.ItemsSource = chatRows.Where(c => c!["workspaceId"]!.GetValue<string>() == owner && c["archived"]?.GetValue<bool>() != true).Select(c => new RemoteItem(c!["id"]!.GetValue<string>(), c["title"]!.GetValue<string>())).ToArray();
            chats.SelectedItem = chats.Items.OfType<RemoteItem>().FirstOrDefault(c => c.Id == selectedId) ?? chats.Items.OfType<RemoteItem>().FirstOrDefault();
        }
        finally { refreshing = false; }
        if (chats.SelectedItem is RemoteItem selected && selected.Id != chatId) SelectChat(selected.Id, userInitiated: false);
        if (chats.SelectedItem is null) { chatId = null; busy = false; preparing = false; UpdateSendAction(); queuedMessages.Children.Clear(); queueJson = ""; messages.Clear(); configs.Children.Clear(); approvals.Children.Clear(); }
    }
    private async Task<JsonNode?> Call(JsonObject request)
    {
        if (connectionCollapsed || connectionSuspended || lifetime.IsCancellationRequested) return null;
        var client = connection;
        if (client is null) { status.IsVisible = true; if (!hostOffline) status.Text = "Reconnecting to the host…"; UpdateSendAction(); return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request["method"]?.GetValue<string>() is "list" or "chat" or "terminal/read" or "file/read" ? 15 : request["method"]?.GetValue<string>() == "delete" ? 120 : 90));
        try { var result = await client.Request(request, timeout.Token); return !lifetime.IsCancellationRequested && !connectionCollapsed && !connectionSuspended && ReferenceEquals(connection, client) ? result : null; }
        catch (RemoteOperationException error)
        {
            status.IsVisible = true; status.Text = error.Message;
            return request["method"]?.GetValue<string>() == "terminal/read" ? new JsonObject { ["error"] = error.Message } : null;
        }
        catch (RemoteRequestBusyException) { return null; }
        catch (Exception error) { if (!connectionCollapsed && !connectionSuspended && !lifetime.IsCancellationRequested) { status.IsVisible = true; status.Text = "Connection interrupted: " + error.Message + " Retrying…"; } client.Dispose(); if (ReferenceEquals(connection, client)) { connection = null; reconnectAfter = DateTimeOffset.UtcNow.AddSeconds(1); } UpdateSendAction(); return null; }
    }
    private void RefreshAttachments()
    {
        UpdateSendAction();
        attachmentChips.Children.Clear();
        foreach (var attachment in attachments)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 6, 4) };
            chip.Children.Add(new TextBlock { Text = attachment.Name, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var remove = new IconButton { Icon = "remove", IconSize = 10, Label = "Remove " + attachment.Name };
            remove.Click += (_, _) => { attachments.Remove(attachment); RefreshAttachments(); };
            chip.Children.Add(remove);
            attachmentChips.Children.Add(chip);
        }
    }
    public async Task ArchiveChat(string id, bool archived)
    {
        await Call(new() { ["method"] = "archive", ["chatId"] = id, ["archived"] = archived }); await RefreshList();
    }
    public async Task UnarchiveChat(string id)
    {
        if (await Call(new() { ["method"] = "archive", ["chatId"] = id, ["archived"] = false }) is null) throw new IOException("Could not unarchive chat. Reconnect and try again.");
        await RefreshList();
    }
    public async Task<string?> DeleteArchivedChat(string id)
    {
        var result = await Call(new() { ["method"] = "delete", ["chatId"] = id }) ?? throw new IOException("Could not delete chat. Reconnect and try again.");
        if (result["deleted"]?.GetValue<bool>() != true) throw new IOException("The host did not confirm deletion.");
        if (chatId == id) { chatId = null; messages.Clear(); messageRevisions.Clear(); busy = preparing = false; UpdateSendAction(); }
        await RefreshList();
        return result["warning"]?.GetValue<string>();
    }
    public void RenameChat(Control anchor, string id, string title)
    {
        var input = new TextBox { Text = title, MinWidth = 200 }; var save = new IconButton { Icon = "send", Label = "Rename chat" };
        var popup = new Flyout { Content = new StackPanel { Spacing = 6, Children = { input, save } } };
        async Task Save() { if (string.IsNullOrWhiteSpace(input.Text)) return; await Call(new() { ["method"] = "rename", ["chatId"] = id, ["title"] = input.Text }); popup.Hide(); await RefreshList(); }
        save.Click += async (_, _) => await Save(); input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Save(); } }; popup.ShowAt(anchor); input.Focus(); input.SelectAll();
    }
    public void ShowImport(Control anchor)
    {
        var menu = new MenuFlyout();
        foreach (var provider in enabledProviders)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (_, _) => { if (workspaces.SelectedItem is RemoteItem owner) { await Call(new() { ["method"] = "import", ["workspaceId"] = owner.Id, ["provider"] = provider.Provider.ToString() }); await RefreshList(); } };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
    public void ShowNewChat(Control anchor, string workspaceId, Action? activate = null)
    {
        var menu = new MenuFlyout();
        foreach (var provider in enabledProviders)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (_, _) =>
            {
                activate?.Invoke();
                var result = await Call(new() { ["method"] = "create", ["workspaceId"] = workspaceId, ["provider"] = provider.Provider.ToString() });
                if (result is not null) { SelectChat(result["id"]!.GetValue<string>()); await RefreshList(); WorkspaceNavigation?.Invoke(); }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        timer.Stop(); lifetime.Cancel(); terminal.Dispose(); foreach (var state in workspaceTerminals.Values) state.View.Dispose(); workspaceTerminals.Clear(); var client = connection; connection = null; client?.Dispose();
        foreach (var permission in notifiedPermissions) PermissionNotifications.Dismiss(host.Address + permission);
        notifiedPermissions.Clear(); messageRevisions.Clear(); CatalogChanged = null; WorkspaceNavigation = null; WorkspaceOpened = null;
        sleepingPage = null; sleepingAnchor = null; messages.Clear(); chatRows.Clear(); configs.Children.Clear(); approvals.Children.Clear(); Content = null;
    }
    private sealed record RemoteItem(string Id, string Name) { public override string ToString() => Name; }
}
