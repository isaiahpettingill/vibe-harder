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

public sealed class RemoteView : UserControl, IDisposable
{
    public Control ConnectionStatus => status;
    public void ReconnectHost() => _ = Connect();
    private Workspace[] workspaceHistory = [];
    private readonly WrapPanel attachmentChips = new();
    private readonly TextBlock attachmentError = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly IconButton send = new() { Name = "RemoteSend", Icon = "send", Label = "Send", Classes = { "accent" } };
    private readonly StackPanel queuedMessages = new() { Spacing = 4 };
    private bool busy, sending, preparing;
    private ListBox output = null!;
    private RemoteTerminalView terminal = null!;
    private string? terminalWorkspace;
    private bool openingTerminal;
    private Point? swipeStart;
    private string queueJson = "";
    private bool canSteer;
    private readonly RemoteHost host;
    public RemoteHost Host => host;
    private RemoteConnection? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox composer = new() { Name = "RemoteComposer", AcceptsReturn = true, MinHeight = 56, MaxHeight = 140, PlaceholderText = "Message the agent…" };
    private readonly StackPanel approvals = new();
    private readonly ListBox chats = new();
    private readonly ComboBox workspaces = new();
    private readonly ObservableCollection<Message> messages = [];
    private string? chatId;
    private bool polling;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string permissionsJson = "";
    private string configJson = "";
    private readonly WrapPanel configs = new() { Orientation = Orientation.Horizontal };
    private readonly List<Attachment> attachments = [];
    private JsonArray chatRows = [];
    private int polls;
    private bool presentationSleeping;
    private bool connecting;
    public void SetPresentationSleeping(bool sleeping)
    {
        if (lifetime.IsCancellationRequested) return;
        var resume = presentationSleeping && !sleeping;
        presentationSleeping = sleeping; terminal.SetSleeping(sleeping);
        if (sleeping) { timer.Stop(); messages.Clear(); }
        else { timer.Start(); if (resume || connection is null) _ = Connect(); }
    }
    private bool refreshing;
    private bool viewingHistory;
    public event Action<JsonNode>? CatalogChanged;
    public event Action? WorkspaceNavigation;
    public event Action<string>? WorkspaceOpened;
    public string? SelectedChatId => chatId;
    public bool HasWorkspace => workspaces.SelectedItem is RemoteItem;
    public void SelectChat(string id)
    {
        viewingHistory = false; output.ItemsSource = messages; busy = false; preparing = true; queueJson = ""; queuedMessages.Children.Clear(); chatId = id; UpdateSendAction(); if (connection is not null) _ = Call(new() { ["method"] = "read", ["chatId"] = id }); messages.Clear(); permissionsJson = ""; configJson = "";
        var owner = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == id)?["workspaceId"]?.GetValue<string>();
        if (owner is not null) workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == owner);
    }
    public RemoteView(RemoteHost host)
    {
        this.host = host;
        var panel = new Grid { RowDefinitions = new("0,*,Auto,Auto"), Margin = new Thickness(12) };
        workspaces.SelectionChanged += (_, _) => FilterChats();
        var split = new Grid { ColumnDefinitions = new("0,0,*") }; Grid.SetRow(split, 1); panel.Children.Add(split);
        chats.SelectionChanged += (_, _) => { if (chats.SelectedItem is RemoteItem selected && chatId != selected.Id) SelectChat(selected.Id); };
        chats.IsVisible = false; split.Children.Add(chats); var divider = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false }; Grid.SetColumn(divider, 1); split.Children.Add(divider);
        output = new ListBox { ItemsSource = messages, ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()), Background = Avalonia.Media.Brushes.Transparent, ItemTemplate = new FuncDataTemplate<Message>((message, _) => { var view = new MessageView { Margin = new Thickness(8) }; view.DataContextChanged += (_, _) => view.Message = view.DataContext as Message; return view; }, true) };
        output.ItemContainerTheme = (Avalonia.Styling.ControlTheme)Application.Current!.Resources["TranscriptItemTheme"]!;
        ScrollViewer.SetVerticalScrollBarVisibility(output, OperatingSystem.IsAndroid() ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
        ScrollViewer.SetAllowAutoHide(output, false);
        Grid.SetColumn(output, 2); split.Children.Add(output);
        var latest = new IconButton { Name = "RemoteLatest", Icon = "chevron-down", Label = "Return to latest message", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 8), IsVisible = false };
        latest.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface")); Grid.SetColumn(latest, 2); split.Children.Add(latest);
        var navigation = new TranscriptNavigation(output, latest, () => viewingHistory, async newer =>
        {
            if (chatId is null || output.Items.Count == 0) return;
            var visible = output.Items.OfType<Message>().ToArray(); var id = chatId;
            var result = await Call(new() { ["method"] = "chat", ["chatId"] = id, [newer ? "after" : "before"] = newer ? visible[^1].Sequence : visible[0].Sequence });
            if (result is null || id != chatId) return;
            var page = result["messages"]!.AsArray().Select(row => ReadMessage(row!)).ToArray();
            if (page.Length == 0) return;
            var merged = visible.Concat(page).GroupBy(m => m.Id).Select(g => g.First()).OrderBy(m => m.Sequence);
            var bounded = newer ? merged.TakeLast(Chat.HistoryPageSize).ToArray() : merged.Take(Chat.HistoryPageSize).ToArray();
            viewingHistory = !(newer && messages.Count > 0 && bounded[^1].Sequence >= messages[^1].Sequence);
            TranscriptNavigation.ReplacePage(output, viewingHistory ? bounded : messages);
        });
        latest.Click += (_, _) => { viewingHistory = false; output.ItemsSource = messages; if (messages.Count > 0) output.ScrollIntoView(messages[^1]); navigation.Update(); };
        Grid.SetRow(approvals, 2); panel.Children.Add(approvals);
        var input = new StackPanel { Spacing = 6 }; Grid.SetRow(input, 3); panel.Children.Add(input); input.Children.Add(new ScrollViewer { Content = queuedMessages, MaxHeight = 120 }); input.Children.Add(attachmentError); input.Children.Add(attachmentChips); input.Children.Add(composer);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 }; input.Children.Add(footer); footer.Children.Add(configs);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Spacing = 4 }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var attach = new IconButton { Name = "RemoteAttach", Icon = "add", Label = "Attach images or files" };
        attach.Click += async (_, _) =>
        {
            attach.IsEnabled = false; attachmentError.Text = "";
            try
            {
                var provider = TopLevel.GetTopLevel(this)!.StorageProvider;
                var selected = await provider.OpenFilePickerAsync(new() { Title = "Attach files", AllowMultiple = true, FileTypeFilter = [FilePickerFileTypes.All] });
                foreach (var file in selected)
                {
                    using (file)
                    {
                        try { attachments.Add(await AttachmentFiles.Read(file, lifetime.Token)); }
                        catch (Exception error) { attachmentError.Text = file.Name + ": " + error.Message; }
                    }
                }
                RefreshAttachments();
            }
            catch (Exception error) { attachmentError.Text = "Could not attach files: " + error.Message; }
            finally { attach.IsEnabled = true; }
        }; actions.Children.Add(attach);
        actions.Children.Add(send);
        composer.TextChanged += (_, _) => UpdateSendAction();
        send.Click += async (_, _) => await SendOrStop();
        composer.KeyDown += async (_, e) => { if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !OperatingSystem.IsAndroid()) { e.Handled = true; if (HasDraft) await SendOrStop(); } };
        UpdateSendAction();
        terminal = new RemoteTerminalView(Call) { IsVisible = false, ZIndex = 20 };
        terminal.Bind(Panel.BackgroundProperty, this.GetResourceObservable("AppBackground"));
        Grid.SetRowSpan(terminal, 4); panel.Children.Add(terminal);
        terminal.Back += () => terminal.SetVisible(false);
        AddHandler(PointerPressedEvent, (_, e) => { if (e.Pointer.Type == PointerType.Touch) swipeStart = e.GetPosition(this); }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch || swipeStart is not { } start) return;
            swipeStart = null; var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < 80 || Math.Abs(delta.X) < Math.Abs(delta.Y) * 1.5) return;
            if (delta.X > 0 && !terminal.IsVisible) await ShowTerminal();
            else if (delta.X < 0 && terminal.IsVisible) terminal.SetVisible(false);
        }, RoutingStrategies.Tunnel, true);
        Content = panel;
        timer.Tick += async (_, _) =>
        {
            if (presentationSleeping || polling || connection is null || chatId is null) return;
            polling = true; var id = chatId;
            try
            {
                var result = await Call(new() { ["method"] = "chat", ["chatId"] = id }); if (presentationSleeping || result is null || id != chatId) return;
                status.Text = result["status"]?.GetValue<string>() + " · " + result["queued"] + " queued";
                busy = result["busy"]?.GetValue<bool>() == true; preparing = result["preparing"]?.GetValue<bool>() ?? (busy && (result["status"]?.GetValue<string>() is { } state && (state.StartsWith("Loading") || state.StartsWith("Connecting") || state.StartsWith("Reconnecting")))); UpdateSendAction();
                UpdateRemoteQueue(result);
                navigation.Update();
                var configText = result["config"]!.ToJsonString();
                if (configText != configJson)
                {
                    configJson = configText; configs.Children.Clear();
                    foreach (var config in result["config"]!.AsArray())
                    {
                        var option = new SessionConfig(config!["id"]!.GetValue<string>(), config["name"]!.GetValue<string>(), "select", config["current"]!.GetValue<string>(), config["values"]!.AsArray().Select(v => new SessionValue(v!["value"]!.GetValue<string>(), v["name"]!.GetValue<string>())).ToArray());
                        var button = new Button { Content = new OptionContent(option), FontSize = 11, MinHeight = OperatingSystem.IsAndroid() ? 40 : 24, Padding = new Thickness(4) }; ToolTip.SetTip(button, config["name"]!.GetValue<string>());
                        button.Click += (_, _) => { var menu = new MenuFlyout(); foreach (var value in config["values"]!.AsArray()) { var item = new MenuItem { Header = value!["name"]!.GetValue<string>() }; item.Click += async (_, _) => await Call(new() { ["method"] = "config", ["chatId"] = chatId, ["configId"] = config["id"]!.DeepClone(), ["value"] = value["value"]!.DeepClone() }); menu.Items.Add(item); } menu.ShowAt(button); }; configs.Children.Add(button);
                    }
                }
                var scroll = output.Scroll;
                var follow = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 80;
                ApplyMessages(result["messages"]!.AsArray());
                if (++polls % 20 == 0) await RefreshList();
                if (follow && !viewingHistory && messages.Count > 0) Dispatcher.UIThread.Post(() => output.ScrollIntoView(messages[^1]));
                var permissionText = result["permissions"]!.ToJsonString();
                if (permissionsJson != permissionText)
                {
                    permissionsJson = permissionText; approvals.Children.Clear();
                    foreach (var permission in result["permissions"]!.AsArray())
                    {
                        approvals.Children.Add(new SelectableTextBlock { Text = permission!["toolCall"]?.ToJsonString(), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 150 });
                        foreach (var option in permission["options"]!.AsArray())
                        {
                            var button = new Button { Content = option!["name"]!.GetValue<string>(), MinHeight = 40 };
                            button.Click += async (_, _) => await Call(new() { ["method"] = "approve", ["permissionId"] = permission["id"]!.DeepClone(), ["optionId"] = option["optionId"]!.DeepClone() }); approvals.Children.Add(button);
                        }
                    }
                }
            }
            finally { polling = false; }
        };
        timer.Start();
        AttachedToVisualTree += async (_, _) => await Connect();
    }
    public async Task ShowTerminal()
    {
        if (openingTerminal || workspaces.SelectedItem is not RemoteItem owner) return;
        openingTerminal = true; terminal.SetVisible(true);
        try
        {
            if (terminalWorkspace != owner.Id || terminal.TerminalId is null)
            {
                await terminal.Open(owner.Id, host.Name + " · " + owner.Name);
                terminalWorkspace = terminal.TerminalId is null ? null : owner.Id;
            }
        }
        finally { openingTerminal = false; }
    }
    private bool HasDraft => !string.IsNullOrWhiteSpace(composer.Text) || attachments.Count > 0;
    private void UpdateSendAction()
    {
        var stop = busy && !preparing && !HasDraft;
        send.Icon = preparing ? "loading" : stop ? "stop" : "send";
        send.Label = preparing ? "Loading chat" : stop ? "Stop" : busy ? "Queue message" : "Send";
        send.IsEnabled = !sending && !preparing && chatId is not null && (stop || HasDraft);
    }
    private async Task SendOrStop()
    {
        if (sending || preparing || chatId is null) return;
        var id = chatId; var text = composer.Text ?? ""; var sent = attachments.ToArray();
        var stop = busy && !preparing && !HasDraft;
        if (!stop && !HasDraft) return;
        sending = true; UpdateSendAction();
        try
        {
            var result = await Call(new() { ["method"] = stop ? "stop" : "send", ["chatId"] = id, ["text"] = text, ["attachments"] = JsonSerializer.SerializeToNode(sent, StoreJsonContext.Default.AttachmentArray) });
            if (result is not null && id == chatId)
            {
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
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
            row.Children.Add(new TextBlock { Text = item["text"]?.GetValue<string>() is { Length: > 0 } text ? text : item["attachments"] + " attachments", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var steer = new IconButton { Name = "RemoteSteerQueued", Icon = "steer", Label = "Steer with queued message", IsVisible = supported };
            var remove = new IconButton { Icon = "remove", Label = "Remove queued message" };
            steer.Click += async (_, _) => { steer.IsEnabled = false; await Call(new() { ["method"] = "queue/steer", ["chatId"] = chat, ["queueId"] = id }); steer.IsEnabled = true; };
            remove.Click += async (_, _) => await Call(new() { ["method"] = "queue/remove", ["chatId"] = chat, ["queueId"] = id });
            Grid.SetColumn(steer, 1); row.Children.Add(steer); Grid.SetColumn(remove, 2); row.Children.Add(remove); queuedMessages.Children.Add(row);
        }
    }
    private static Message ReadMessage(JsonNode row)
    {
        var message = new Message { Id = row["id"]!.GetValue<string>(), Role = row["role"]!.GetValue<string>(), Sequence = row["sequence"]?.GetValue<int>() ?? 0, Text = row["text"]!.GetValue<string>() };
        foreach (var file in row["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? []) message.Attachments.Add(file);
        return message;
    }
    private void ApplyMessages(JsonArray rows)
    {
        if (presentationSleeping) return;
        foreach (var row in rows)
        {
            var id = row!["id"]!.GetValue<string>(); var message = messages.FirstOrDefault(m => m.Id == id);
            if (message is null) { message = new Message { Id = id, Role = row["role"]!.GetValue<string>(), Sequence = row["sequence"]?.GetValue<int>() ?? 0 }; messages.Add(message); }
            message.Text = row["text"]!.GetValue<string>();
            if (message.Attachments.Count == 0 && row["attachments"] is { } files)
                foreach (var file in files.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? []) message.Attachments.Add(file);
        }
        while (messages.Count > Chat.HistoryPageSize) messages.RemoveAt(0);
    }
    private async Task Connect()
    {
        if (connecting || lifetime.IsCancellationRequested) return;
        connecting = true;
        connection?.Dispose(); connection = null;
        RemoteConnection? candidate = null;
        try { candidate = new RemoteConnection(host); await candidate.Connect(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested(); connection = candidate; status.Text = "Connected"; status.IsVisible = false; await RefreshList(); }
        catch (Exception error) { status.IsVisible = true; status.Text = error.Message + (candidate?.ObservedFingerprint is { } pin ? "\nObserved host fingerprint: " + pin + "\nVerify it on the host before changing the saved fingerprint." : ""); candidate?.Dispose(); }
        finally { connecting = false; }
    }
    private async Task RefreshList()
    {
        var result = await Call(new() { ["method"] = "list" }); if (result is null) return;
        workspaceHistory = result["workspaces"]!.AsArray().Select(w => new Workspace(w!["id"]!.GetValue<string>(), w["name"]!.GetValue<string>(), w["path"]?.GetValue<string>() ?? "", w["distro"]?.GetValue<string>())).ToArray();
        chatRows = result["chats"]!.AsArray();
        var selected = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == chatId)?["workspaceId"]?.GetValue<string>() ?? (workspaces.SelectedItem as RemoteItem)?.Id;
        selected ??= chatRows.FirstOrDefault(c => c?["archived"]?.GetValue<bool>() != true)?["workspaceId"]?.GetValue<string>();
        refreshing = true;
        workspaces.ItemsSource = result["workspaces"]!.AsArray().Select(w => new RemoteItem(w!["id"]!.GetValue<string>(), w["name"]!.GetValue<string>())).ToArray();
        workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == selected) ?? workspaces.Items.OfType<RemoteItem>().FirstOrDefault();
        refreshing = false;
        FilterChats();
        CatalogChanged?.Invoke(result.DeepClone());
    }
    private void FilterChats()
    {
        if (refreshing) return;
        var owner = (workspaces.SelectedItem as RemoteItem)?.Id;
        chats.ItemsSource = chatRows.Where(c => c!["workspaceId"]!.GetValue<string>() == owner && (c["archived"]?.GetValue<bool>() != true || c["id"]!.GetValue<string>() == chatId)).Select(c => new RemoteItem(c!["id"]!.GetValue<string>(), c["title"]!.GetValue<string>())).ToArray();
        chats.SelectedItem = chats.Items.OfType<RemoteItem>().FirstOrDefault(c => c.Id == chatId) ?? chats.Items.OfType<RemoteItem>().FirstOrDefault();
        if (chats.SelectedItem is null) { chatId = null; busy = false; UpdateSendAction(); queuedMessages.Children.Clear(); queueJson = ""; messages.Clear(); configs.Children.Clear(); approvals.Children.Clear(); }
    }
    private async Task<JsonNode?> Call(JsonObject request)
    {
        var client = connection;
        if (client is null) { status.Text = "Connect to the host first."; return null; }
        try { return await client.Request(request, lifetime.Token); }
        catch (RemoteOperationException error) { status.IsVisible = true; status.Text = error.Message; return null; }
        catch (Exception error) { status.IsVisible = true; status.Text = "Disconnected: " + error.Message + " Agents remain on the host. Reconnect to continue."; client.Dispose(); if (ReferenceEquals(connection, client)) connection = null; return null; }
    }
    private void RefreshAttachments()
    {
        UpdateSendAction();
        attachmentChips.Children.Clear();
        foreach (var attachment in attachments)
        {
            var chip = new Button { Content = attachment.Name + " ×", MinHeight = 40, MaxWidth = 260 };
            ToolTip.SetTip(chip, "Remove " + attachment.Name);
            chip.Click += (_, _) => { attachments.Remove(attachment); RefreshAttachments(); };
            attachmentChips.Children.Add(chip);
        }
    }
    public async Task ArchiveChat(string id, bool archived)
    {
        await Call(new() { ["method"] = "archive", ["chatId"] = id, ["archived"] = archived }); await RefreshList();
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
        foreach (var provider in AgentProviders.All)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (_, _) => { if (workspaces.SelectedItem is RemoteItem owner) { await Call(new() { ["method"] = "import", ["workspaceId"] = owner.Id, ["provider"] = provider.Provider.ToString() }); await RefreshList(); } };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
    public void ShowNewChat(Control anchor, string workspaceId)
    {
        var menu = new MenuFlyout();
        foreach (var provider in AgentProviders.All)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += async (_, _) =>
            {
                var result = await Call(new() { ["method"] = "create", ["workspaceId"] = workspaceId, ["provider"] = provider.Provider.ToString() });
                if (result is not null) { SelectChat(result["id"]!.GetValue<string>()); await RefreshList(); WorkspaceNavigation?.Invoke(); }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
    public void ShowWorkspacePicker(Control anchor)
    {
        var selector = new WorkspaceSelector(workspaceHistory, allowRemoval: false) { Width = Math.Min(330, Math.Max(220, Bounds.Width - 24)) };
        var menu = new Flyout { Content = selector };
        selector.Chosen += workspace => { WorkspaceOpened?.Invoke(workspace.Id); workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == workspace.Id); menu.Hide(); WorkspaceNavigation?.Invoke(); };
        selector.Browse += () =>
        {
            menu.Hide(); WorkspaceNavigation?.Invoke();
            var browser = new RemoteWorkspacePicker(Call);
            var overlay = new Border { Background = Background, Child = browser, ZIndex = 100 };
            var grid = (Grid)Content!; Grid.SetRowSpan(overlay, 4); grid.Children.Add(overlay);
            browser.Closed += async () => { grid.Children.Remove(overlay); await RefreshList(); if (browser.OpenedWorkspaceId is { } id) { WorkspaceOpened?.Invoke(id); workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == id); } };
        };
        menu.ShowAt(anchor);
    }
    public void Dispose() { timer.Stop(); terminal.Dispose(); var client = connection; connection = null; lifetime.Cancel(); if (client is not null) _ = CloseConnection(client, terminal.TerminalId); CatalogChanged = null; WorkspaceNavigation = null; WorkspaceOpened = null; messages.Clear(); chatRows.Clear(); configs.Children.Clear(); approvals.Children.Clear(); Content = null; }
    private static async Task CloseConnection(RemoteConnection client, string? terminalId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { if (terminalId is not null) await client.Request(new() { ["method"] = "terminal/close", ["terminalId"] = terminalId }, timeout.Token); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); }
        finally { client.Dispose(); }
    }
    private sealed record RemoteItem(string Id, string Name) { public override string ToString() => Name; }
}
