using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SvcSystems.UI.Terminal;

namespace CodexManager;

public partial class MainView : UserControl
{
    private readonly Store store;
    private SessionService remoteSessions = null!;
    private RemoteServer? remoteServer;
    private RemoteView? remoteView;
    private readonly Dictionary<string, StackPanel> remoteSections = [];
    private readonly Dictionary<string, Chat> remoteActivity = [];
    private async Task ConfigureRemoteServer()
    {
        if (remoteServer is not null) { await remoteServer.DisposeAsync(); remoteServer = null; }
        if (!remoteOnly && store.Setting("remoteEnabled") != "0")
        {
            try
            {
                remoteServer = new RemoteServer(RemoteServer.DirectoryPath, store.Setting("remoteListenAddress") ?? "0.0.0.0", int.TryParse(store.Setting("remotePort"), out var port) ? port : 2222, remoteSessions.Handle, ShowPairingCode);
                while (remoteServer.Fingerprint is null && remoteServer.Error is null && !closing) await Task.Delay(25, discoveryLifetime.Token);
                if (remoteServer.Error is { } error) StatusText.Text = "Remote server: " + error;
            }
            catch (Exception error) { StatusText.Text = "Remote server: " + error.Message; }
        }
    }
    private void CloseRemoteView()
    { if (remoteView is null) return; remoteView.Dispose(); RootPanes.Children.Remove(remoteView); remoteView = null; }
    private void OpenRemoteHost(RemoteHost host)
    {
        CollapseSidebar(); CloseRemoteView(); var view = new RemoteView(host); remoteView = view;
        if (current is not null) DeferHistoryEviction(current);
        MessageList.ItemsSource = null; AttachmentList.ItemsSource = null;
        view.CatalogChanged += catalog =>
        {
            if (!remoteSections.TryGetValue(host.Name, out var section)) return;
            section.Children.Clear();
            foreach (var workspace in catalog["workspaces"]!.AsArray())
            {
                var ownerId = workspace!["id"]!.GetValue<string>(); var key = "collapsed:remote:" + host.Address + ":" + ownerId;
                var group = new StackPanel { IsVisible = store.Setting(key) != "1" };
                var heading = new Button { Content = (group.IsVisible ? "▾ " : "▸ ") + workspace["name"]!.GetValue<string>(), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                heading.Click += (_, _) => { group.IsVisible = !group.IsVisible; store.Setting(key, group.IsVisible ? "0" : "1"); heading.Content = (group.IsVisible ? "▾ " : "▸ ") + workspace["name"]!.GetValue<string>(); };
                section.Children.Add(heading); section.Children.Add(group);
                foreach (var chat in catalog["chats"]!.AsArray().Where(c => c!["workspaceId"]!.GetValue<string>() == workspace["id"]!.GetValue<string>() && !c["archived"]!.GetValue<bool>()))
                {
                    var id = chat!["id"]!.GetValue<string>();
                    var activityKey = host.Address + ":" + id;
                    if (!remoteActivity.TryGetValue(activityKey, out var activity)) remoteActivity[activityKey] = activity = new Chat { Id = id, WorkspaceId = ownerId, Provider = Enum.Parse<AgentProvider>(chat["provider"]!.GetValue<string>()) };
                    activity.Busy = chat["busy"]!.GetValue<bool>(); activity.HasUnreadCompletion = chat["unread"]?.GetValue<bool>() == true;
                    var row = new Grid { ColumnDefinitions = new("22,*") }; row.Children.Add(new ChatActivityIndicator(activity)); var title = new TextBlock { Text = chat["title"]!.GetValue<string>(), TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(title, 1); row.Children.Add(title);
                    var choose = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    choose.Click += (_, _) => { activity.HasUnreadCompletion = false; if (remoteView?.Host != host) OpenRemoteHost(host); remoteView!.SelectChat(id); CollapseSidebar(); }; group.Children.Add(choose);
                }
            }
        };
        Grid.SetColumn(view, 2); Grid.SetRow(view, 1); view.Bind(BackgroundProperty, this.GetResourceObservable("AppBackground")); RootPanes.Children.Add(view);
    }

    private readonly ObservableCollection<Workspace> workspaces;
    private readonly List<Chat> chats;
    private readonly Dictionary<string, ChatRuntime> runtimes = [];
    private readonly Dictionary<string, List<(TabItem Tab, TerminalSession Session)>> terminals = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Workspace? workspace;
    private Chat? current;
    private bool switching;
    private bool closing;
    private bool exitRequested;
    private TrayIcon? tray;
    private bool recoveryOffered;
    private bool showArchived;
    private Task? historyOperation;
    private readonly CancellationTokenSource discoveryLifetime = new();
    private readonly Dictionary<string, Task> discoveries = [];
    private readonly Dictionary<string, bool> authentication = [];
    private readonly Dictionary<string, (TerminalControl Control, TerminalSession Session)> loginSessions = [];
    private readonly Dictionary<string, ListBox> workspaceLists = [];
    private readonly HashSet<Task> workspaceClosures = [];
    private readonly ListBox emptyChatList = new();
    private readonly ScrollViewer emptyTranscriptScroll = new();
    private ScrollViewer TranscriptScroll => MessageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() ?? emptyTranscriptScroll;
    private ListBox ChatList => workspace is null ? emptyChatList : workspaceLists.GetValueOrDefault(workspace.Id) ?? emptyChatList;
    private bool refreshingChats;
    private CancellationTokenSource? pageLoad;
    private bool viewingHistory;
    private HashSet<string> searchMatches = [];
    private CancellationTokenSource? searchCancellation;
    private Chat? configChat;
    private int configVersion = -1;
    private DateTimeOffset lastEscape;
    private Chat? escapeChat;
    private PendingInput[] displayedQueue = [];
    private Chat? queueChat;
    private CommandPalette? palette;
    private double? resizeY;
    private double resizeHeight;

    public MainView() : this(new Store()) { }
    public MainView(Store store, List<Workspace>? loadedWorkspaces = null, List<Chat>? loadedChats = null, bool remoteOnly = false)
    {
        this.store = store; this.remoteOnly = remoteOnly;
        InitializeComponent();
        TerminalDrawer.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            ToggleTerminalButton.Icon = TerminalDrawer.IsVisible ? "chevron-left" : "terminal";
            ToggleTerminalButton.Label = TerminalDrawer.IsVisible ? "Hide terminal (Ctrl+`)" : "Show terminal (Ctrl+`)";
        };
        FontSettings.Apply(store); AppTheme.Apply(store);
        if (double.TryParse(store.Setting("terminalWidth"), System.Globalization.CultureInfo.InvariantCulture, out var terminalWidth)) TerminalDrawer.Width = Math.Clamp(terminalWidth, 220, 800);
        RootPanes.ColumnDefinitions[0].MinWidth = 170; RootPanes.ColumnDefinitions[0].MaxWidth = 600;
        RootPanes.ColumnDefinitions[2].MinWidth = 420;
        if (double.TryParse(store.Setting("sidebarWidth"), System.Globalization.CultureInfo.InvariantCulture, out var sidebarWidth)) RootPanes.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(sidebarWidth, 170, 600));
        workspaces = new((remoteOnly ? [] : loadedWorkspaces ?? store.Workspaces()).Where(w => store.Setting("closed:" + w.Id) != "1")); chats = remoteOnly ? [] : loadedChats ?? store.Chats();
        foreach (var savedChat in chats) savedChat.RetainHistory = false;
        InitializePresentationSleep();
        remoteSessions = new SessionService(store, workspaces, chats, Runtime);
        remoteSessions.Changed += BuildWorkspaceTree;
        BuildWorkspaceTree();
        DragDrop.SetAllowDrop(ComposerBorder, true);
        ComposerBorder.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        ComposerBorder.AddHandler(DragDrop.DropEvent, DropFiles);
        Composer.AddHandler(KeyDownEvent, ComposerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Oem3 && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; ToggleTerminal(this, new()); }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Escape || current?.Busy != true || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            if (SlashCommands.IsVisible) { SlashCommands.IsVisible = false; return; }
            var twice = ReferenceEquals(escapeChat, current) && DateTimeOffset.UtcNow - lastEscape < TimeSpan.FromMilliseconds(650);
            lastEscape = DateTimeOffset.UtcNow; escapeChat = current;
            if (twice) { lastEscape = default; StopClick(this, new()); }
            else { Composer.Focus(); await SteerDraft(); }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; PaletteClick(this, new()); }
        }, RoutingStrategies.Tunnel);
        saveTimer.Tick += async (_, _) => { try { SaveAll(); await store.FlushAsync(); } catch (Exception error) { StatusText.Text = "Could not save: " + error.Message; } }; saveTimer.Start();
        InitializeLayout();
        if (workspaces.Count > 0) SelectWorkspace(workspaces.FirstOrDefault(w => w.Id == store.Setting("workspace")) ?? workspaces[0]);
        UpdateControls();
    }
    private async void OpenWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (remoteOnly) { ShowConnectionSettings(); return; }
        var dialog = new WorkspaceDialog(workspace, store);
        var selected = await dialog.ShowDialog<Workspace?>(desktopWindow!);
        if (dialog.PairedHost is { } host) { BuildWorkspaceTree(); OpenRemoteHost(host); return; }
        if (selected is null) return;
        OpenWorkspace(selected);
    }
    private void OpenWorkspace(Workspace selected)
    {
        showArchived = false; ArchiveViewButton.Content = "Chats ▾"; SearchBox.Text = "";
        var existing = store.Workspaces().FirstOrDefault(w => w.Path == selected.Path && w.Distro == selected.Distro) ?? selected;
        store.Save(existing); store.Setting("closed:" + existing.Id, "0");
        if (!workspaces.Any(w => w.Id == existing.Id)) workspaces.Add(existing);
        BuildWorkspaceTree(); SelectWorkspace(existing, true);
    }
    private async void WorkspaceSelectorClick(object? sender, RoutedEventArgs e)
    {
        var history = new WorkspaceHistory(store);
        var selector = new WorkspaceSelector(history.Entries());
        var flyout = new Flyout { Content = selector };
        selector.Removed += item => { history.Remove(item); selector.Refresh(history.Entries()); };
        selector.Chosen += async item =>
        {
            if (await WorkspaceHistory.Exists(item) == false)
            { if (!closing) { history.Remove(item); selector.Refresh(history.Entries()); } return; }
            if (closing) return;
            flyout.Hide(); OpenWorkspace(item);
        };
        selector.Browse += () => { flyout.Hide(); OpenWorkspaceClick(this, new()); };
        flyout.Opened += (_, _) => selector.FocusSearch();
        Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(WorkspaceSelectorButton, flyout);
        flyout.ShowAt(WorkspaceSelectorButton);
        foreach (var item in history.Entries())
        {
            if (await WorkspaceHistory.Exists(item) == false && !closing) history.Remove(item);
            if (closing) return;
        }
        selector.Refresh(history.Entries());
    }
    private void SelectWorkspace(Workspace selected, bool startChat = false)
    {
        CloseRemoteView();
        if (workspace?.Id != selected.Id) ClearChat();
        workspace = selected; store.Setting("workspace", selected.Id);
        if (startChat) new WorkspaceHistory(store).Opened(selected);
        WorkspaceHeading.Text = $"{selected.Host}  /  {selected.Path}";
        RefreshChats();
        ChatList.SelectedItem = chats.FirstOrDefault(c => c.WorkspaceId == selected.Id && c.Archived == showArchived && c.Id == store.Setting("chat:" + selected.Id)) ?? ChatList.Items.FirstOrDefault();
        if (ChatList.SelectedItem is null) ClearChat();
        TerminalTabs.ItemsSource = terminals.GetValueOrDefault(selected.Id)?.Select(t => t.Tab).ToArray();
        if (TerminalTabs.ItemCount > 0) TerminalTabs.SelectedIndex = 0;
        UpdateControls();
        if (startChat && !chats.Any(c => c.WorkspaceId == selected.Id && !c.Archived))
            NewChat(Enum.TryParse<AgentProvider>(store.Setting("lastProvider"), out var provider) ? provider : AgentProvider.Codex);
        foreach (var provider in AgentProviders.All)
        {
            var key = selected.Id + ":" + provider.Provider;
            if (!discoveries.ContainsKey(key)) discoveries[key] = DiscoverHistory(selected, provider.Provider);
        }
    }
    private async Task<string> DiscoverHistory(Workspace owner, AgentProvider provider = AgentProvider.Codex)
    {
        try
        {
            var found = await ChatHistory.Discover(owner, AgentProviders.Command(store, owner, provider), discoveryLifetime.Token, provider, needsLogin => Dispatcher.UIThread.Post(() => { if (!closing) { authentication[$"{owner.Distro}:{provider}"] = needsLogin; UpdateControls(); } }));
            if (closing) return "";
            var added = 0;
            foreach (var chat in found)
            {
                if (chats.Any(c => c.WorkspaceId == owner.Id && c.Provider == provider && c.SessionId == chat.SessionId)) continue;
                if (store.Setting(AgentProviders.HiddenHistoryKey(chat)) == "1") continue;
                store.Save(chat); chats.Add(chat); added++;
            }
            RefreshChats();
            if (workspace?.Id == owner.Id)
            {
                var selectedChat = current;
                if (selectedChat is not null && ChatList.Items.Contains(selectedChat)) ChatList.SelectedItem = selectedChat;
                StatusText.Text = $"{AgentProviders.Get(provider).Name}: imported {added} previous chats.";
            }
            return $"{AgentProviders.Get(provider).Name}: imported {added} previous chats.";
        }
        catch (OperationCanceledException) when (closing) { return ""; }
        catch (Exception error)
        {
            if (AgentProviders.IsAuthenticationError(error)) authentication[$"{owner.Distro}:{provider}"] = true;
            if (!closing) UpdateControls();
            var message = $"Could not import {AgentProviders.Get(provider).Name} chats: " + error.Message;
            if (!closing && workspace?.Id == owner.Id) StatusText.Text = message;
            return message;
        }
    }
    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (chats is null) return;
        searchCancellation?.Cancel(); var cancellation = searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        searchMatches.Clear(); RefreshChats(); var query = SearchBox.Text ?? ""; if (query.Length == 0) return;
        try { await Task.Delay(150, cancellation.Token); var matches = await store.SearchChatIdsAsync(query, cancellation.Token); if (!cancellation.IsCancellationRequested) { searchMatches = matches; RefreshChats(); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Search failed: " + error.Message; }
    }
    private void BuildWorkspaceTree()
    {
        WorkspaceTree.Children.Clear(); workspaceLists.Clear();
        foreach (var owner in workspaces)
        {
            var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto") };
            var title = new Button { Name = "Workspace_" + owner.Id, Content = owner.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(title, owner.Caption + " · " + owner.Path);
            title.Click += (_, _) => SelectWorkspace(owner, true);
            var create = new Button { Name = "NewChat_" + owner.Id, Content = "＋", Padding = new(5, 2) };
            ToolTip.SetTip(create, "New chat"); Grid.SetColumn(create, 2);
            create.Flyout = ProviderMenu(owner);
            var close = new Button { Name = "CloseWorkspace_" + owner.Id, Content = "×", Padding = new(5, 2) };
            ToolTip.SetTip(close, "Close workspace (keep chats)"); Grid.SetColumn(close, 3);
            close.Click += async (_, _) =>
            {
                close.IsEnabled = false;
                var operation = CloseWorkspace(owner); workspaceClosures.Add(operation);
                try { await operation; }
                finally { workspaceClosures.Remove(operation); }
            };
            header.Children.Add(title); header.Children.Add(create); header.Children.Add(close);
            var list = new ListBox { Name = "Chats_" + owner.Id, Background = Brushes.Transparent, Tag = owner, Margin = new(8, 0, 0, 0) };
            list.IsVisible = store.Setting("collapsed:" + owner.Id) != "1";
            var collapse = new IconButton { Name = "CollapseWorkspace_" + owner.Id, Icon = list.IsVisible ? "chevron-down" : "chevron-right", Label = list.IsVisible ? "Collapse workspace" : "Expand workspace" };
            collapse.Click += (_, _) => { list.IsVisible = !list.IsVisible; collapse.Icon = list.IsVisible ? "chevron-down" : "chevron-right"; collapse.Label = list.IsVisible ? "Collapse workspace" : "Expand workspace"; store.Setting("collapsed:" + owner.Id, list.IsVisible ? "0" : "1"); };
            Grid.SetColumn(title, 1); header.Children.Add(collapse);
            list.ItemTemplate = new FuncDataTemplate<Chat>((chat, _) =>
            {
                if (chat is null) return null;
                var row = new Grid { ColumnDefinitions = new("20,*,Auto,Auto"), Margin = new(0, 4) };
                row.Children.Add(new ChatActivityIndicator(chat) { Name = "Activity_" + chat.Id, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 0, 0) });
                var details = new StackPanel { Spacing = 3 };
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, CompiledBinding.Create((Chat c) => c.Title, source: chat));
                var status = new TextBlock { FontSize = 11, Classes = { "muted" } };
                status.Bind(TextBlock.TextProperty, CompiledBinding.Create((Chat c) => c.Status, source: chat));
                details.Children.Add(name); details.Children.Add(status); Grid.SetColumn(details, 1); row.Children.Add(details);
                var rename = new IconButton { Name = "Rename_" + chat.Id, Icon = "edit", Label = "Rename chat", MinWidth = 23, MinHeight = 23, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top };
                rename.Click += async (_, e) => { e.Handled = true; await RenameChat(chat); }; Grid.SetColumn(rename, 2); row.Children.Add(rename);
                var archive = new IconButton { Name = "Archive_" + chat.Id, Icon = "archive", Label = chat.Archived ? "Restore chat" : "Archive chat", MinWidth = 23, MinHeight = 23, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top };
                archive.Click += async (_, e) => { e.Handled = true; await ArchiveChat(chat); }; Grid.SetColumn(archive, 3); row.Children.Add(archive);
                rename.MinWidth = archive.MinWidth = 20; rename.MinHeight = archive.MinHeight = 20;
                row.Background = Brushes.Transparent; row.Classes.Add("chatRow"); rename.Classes.Add("rowAction"); archive.Classes.Add("rowAction");
                ToolTip.SetTip(row, chat.ProviderLabel); return row;
            }, false);
            list.SelectionChanged += ChatChanged; workspaceLists[owner.Id] = list;
            WorkspaceTree.Children.Add(new StackPanel { Children = { header, list } });
        }
        foreach (var host in RemoteSettings.Hosts(store))
        {
            var button = new Button { Content = "Remote · " + host.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => OpenRemoteHost(host); WorkspaceTree.Children.Add(button);
            var section = new StackPanel(); remoteSections[host.Name] = section; WorkspaceTree.Children.Add(section);
        }
        RefreshChats();
    }
    private MenuFlyout ProviderMenu(Workspace owner)
    {
        var menu = new MenuFlyout();
        foreach (var provider in new[] { AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode })
        {
            var item = new MenuItem { Header = AgentProviders.Get(provider).Name, Icon = new Image { Source = BrandAssets.Provider(provider), Width = 16, Height = 16 } };
            item.Click += (_, _) => { SelectWorkspace(owner); NewChat(provider); };
            menu.Items.Add(item);
        }
        return menu;
    }
    private async Task CloseWorkspace(Workspace owner)
    {
        SaveAll(); store.Setting("closed:" + owner.Id, "1");
        if (workspace?.Id == owner.Id) { ClearChat(); workspace = null; }
        workspaces.Remove(owner);
        if (terminals.Remove(owner.Id, out var shells)) foreach (var shell in shells) shell.Session.Dispose();
        foreach (var key in loginSessions.Keys.Where(k => k.StartsWith(owner.Id + ":", StringComparison.Ordinal)).ToArray())
        { loginSessions[key].Session.Dispose(); loginSessions.Remove(key); }
        foreach (var chat in chats.Where(c => c.WorkspaceId == owner.Id).ToArray())
            if (runtimes.Remove(chat.Id, out var runtime)) await runtime.DisposeAsync();
        SaveAll();
        if (closing) return;
        BuildWorkspaceTree();
        if (workspace is not null) SelectWorkspace(workspace);
        else if (workspaces.FirstOrDefault() is { } next) SelectWorkspace(next);
        else { store.Setting("workspace", ""); WorkspaceHeading.Text = "No workspace selected"; TerminalTabs.ItemsSource = null; TerminalDrawer.IsVisible = false; UpdateControls(); }
    }
    private void RefreshChats()
    {
        var query = SearchBox.Text ?? "";
        refreshingChats = true;
        foreach (var (id, list) in workspaceLists)
        {
            var selected = list.SelectedItem;
            list.ItemsSource = chats.Where(c => c.WorkspaceId == id && c.Archived == showArchived && (c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || searchMatches.Contains(c.Id))).OrderByDescending(c => c.Updated).ToArray();
            if (selected is not null && list.Items.Contains(selected)) list.SelectedItem = selected;
        }
        refreshingChats = false;
    }
    private void NewChat(AgentProvider provider = AgentProvider.Codex)
    {
        if (workspace is null) return;
        showArchived = false; ArchiveViewButton.Content = "Chats ▾";
        var chat = new Chat { WorkspaceId = workspace.Id, Provider = provider }; store.Save(chat); chats.Insert(0, chat);
        SearchBox.Text = ""; RefreshChats(); ChatList.SelectedItem = chat;
        _ = Runtime(chat, workspace).Reconnect();
    }
    private async void ChatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingChats || sender is not ListBox { SelectedItem: Chat chat, Tag: Workspace owner }) return;
        CloseRemoteView();
        if (workspace?.Id != owner.Id)
        {
            if (current is not null) { current.Draft = Composer.Text ?? ""; store.Save(current); }
            workspace = owner; store.Setting("workspace", owner.Id); WorkspaceHeading.Text = $"{owner.Host}  /  {owner.Path}";
            TerminalTabs.ItemsSource = terminals.GetValueOrDefault(owner.Id)?.Select(t => t.Tab).ToArray();
            if (TerminalTabs.ItemCount > 0) TerminalTabs.SelectedIndex = 0;
        }
        refreshingChats = true;
        foreach (var list in workspaceLists.Values.Where(l => l != sender)) list.SelectedItem = null;
        refreshingChats = false;
        if (current is not null) { current.Draft = Composer.Text ?? ""; store.Save(current); }
        chat.HasUnreadCompletion = false; store.Save(chat);
        if (current is not null && !ReferenceEquals(current, chat)) DeferHistoryEviction(current);
        KeepHistory(chat);
        switching = true; current = chat; Composer.Text = chat.Draft; switching = false;
        store.Setting("chat:" + chat.WorkspaceId, chat.Id);
        store.Setting("lastProvider", chat.Provider.ToString());
        MessageList.ItemsSource = runtimes.TryGetValue(chat.Id, out var loadingRuntime) && loadingRuntime.IsLoadingHistory ? chat.Messages.ToArray() : chat.Messages; AttachmentList.ItemsSource = chat.Attachments;
        UpdateControls(); Composer.Focus();
        Dispatcher.UIThread.Post(() => ScrollTranscriptToEnd(), DispatcherPriority.Background);
        viewingHistory = false; pageLoad?.Cancel(); pageLoad = CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        if (!chat.HistoryLoaded && runtimes.GetValueOrDefault(chat.Id)?.IsLoadingHistory != true)
        {
            try
            {
                await RestoreVisibleHistory(chat, pageLoad.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { StatusText.Text = "Could not load history: " + error.Message; return; }
        }
        if (chat.SessionId is not null && (chat.Messages.Count == 0 || store.Setting("historyIncomplete:" + chat.Id) == "1") && workspace is not null)
            await Runtime(chat, workspace).LoadHistory();
        else if (chat.SessionId is not null && workspace is not null && Runtime(chat, workspace) is { IsConnected: false, IsReconnecting: false } runtime)
            await runtime.Reconnect();
    }
    private void DraftChanged(object? sender, TextChangedEventArgs e) { if (!switching && current is not null) current.Draft = Composer.Text ?? ""; UpdateSlashCommands(); }
    private void UpdateSlashCommands()
    {
        var matches = SlashCommand.Match(current?.Commands ?? [], Composer.Text ?? "");
        var selected = SlashCommands.SelectedItem as SlashCommand;
        SlashCommands.ItemsSource = matches;
        SlashCommands.SelectedItem = matches.FirstOrDefault(c => c.Name == selected?.Name) ?? matches.FirstOrDefault();
        SlashCommands.IsVisible = matches.Count > 0;
    }
    private async Task RenameChat(Chat chat)
    {
        var dialog = new Window { Title = "Rename chat", Width = 420, Height = 145, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Name = "ChatTitleInput", Text = chat.Title, MaxLength = 250 };
        var save = new IconButton { Icon = "send", Label = "Save title", HorizontalAlignment = HorizontalAlignment.Right };
        void Apply() { if (string.IsNullOrWhiteSpace(input.Text)) return; chat.Title = input.Text.Trim(); store.Save(chat); UpdateControls(); dialog.Close(); }
        save.Click += (_, _) => Apply(); input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Apply(); } };
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { input, save } };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); }; await dialog.ShowDialog(desktopWindow!);
    }
    private void InsertSlashCommand()
    {
        if (SlashCommands.SelectedItem is not SlashCommand command) return;
        Composer.Text = "/" + command.Name + " "; Composer.CaretIndex = Composer.Text.Length;
        SlashCommands.IsVisible = false; Composer.Focus();
    }
    private void SlashCommandClicked(object? sender, PointerReleasedEventArgs e) => InsertSlashCommand();
    private void UpdateQueue()
    {
        var inputs = current?.QueuedInputs.ToArray() ?? [];
        QueuePanel.IsVisible = inputs.Length > 0;
        QueuePanel.Header = $"{inputs.Length} queued message{(inputs.Length == 1 ? "" : "s")}";
        if (ReferenceEquals(queueChat, current) && displayedQueue.SequenceEqual(inputs)) return;
        queueChat = current; displayedQueue = inputs; QueueItems.Children.Clear();
        if (current is not { } chat || workspace is not { } owner) return;
        foreach (var input in inputs)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(2) };
            row.Children.Add(new TextBlock { Text = input.Text.Length > 0 ? input.Text : $"{input.Attachments.Length} attachments", TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
            void Action(int column, string icon, string label, Func<Task> action)
            {
                var button = new IconButton { Icon = icon, Label = label };
                button.Click += async (_, _) => await action(); Grid.SetColumn(button, column); row.Children.Add(button);
            }
            Action(1, "remove", "Remove queued message", () => { Runtime(chat, owner).RemoveQueued(input); return Task.CompletedTask; });
            Action(2, "edit", "Edit queued message", () => { Runtime(chat, owner).RemoveQueued(input); chat.RecoverInput(input); if (ReferenceEquals(chat, current)) { Composer.Text = chat.Draft; Composer.Focus(); } store.Save(chat); return Task.CompletedTask; });
            Action(3, "steer", "Steer current turn (when supported by ACP)", async () =>
            {
                var runtime = Runtime(chat, owner);
                if (await runtime.Steer(input)) runtime.RemoveQueued(input);
                else { chat.Status = "Message remains queued — steering unavailable or not accepted"; UpdateControls(); }
            });
            Action(4, "send", "Send now (interrupt current turn)", () => Runtime(chat, owner).SendQueuedNow(input));
            QueueItems.Children.Add(row);
        }
    }
    private async Task SteerDraft()
    {
        if (current is not { } chat || workspace is not { } owner) return;
        var runtime = Runtime(chat, owner);
        if (!runtime.SupportsSteering) { chat.Status = "ACP steering unavailable — Enter queues; Escape again stops"; UpdateControls(); return; }
        var text = Composer.Text ?? ""; var attachments = chat.Attachments.ToArray();
        if (string.IsNullOrWhiteSpace(text) && attachments.Length == 0)
        {
            if (chat.QueuedInputs.FirstOrDefault() is { } queued && await runtime.Steer(queued)) runtime.RemoveQueued(queued);
            return;
        }
        if (await runtime.Steer(new(text, attachments)))
        {
            if (ReferenceEquals(chat, current) && Composer.Text == text) { Composer.Text = ""; chat.Draft = ""; }
            foreach (var attachment in attachments) chat.Attachments.Remove(attachment);
            store.Save(chat); UpdateControls();
        }
    }
    private void UpdateControls()
    {
        if (uiSleeping) { UpdateTray(); return; }
        UpdateTray();
        UpdateSlashCommands();
        UpdateQueue();
        ConfigOptionsPanel.IsEnabled = current is not null && runtimes.GetValueOrDefault(current.Id) is { IsConnected: true, IsConfiguring: false, IsReconnecting: false, IsLoadingHistory: false };
        if (!ReferenceEquals(configChat, current) || configVersion != (current?.ConfigVersion ?? -1))
        {
            configChat = current; configVersion = current?.ConfigVersion ?? -1; ConfigOptionsPanel.Children.Clear();
            if (current is { } configured && workspace is { } owner)
                foreach (var option in configured.ConfigOptions)
                {
                    var picker = new Button { Name = "Config_" + option.Id, Content = new OptionContent(option), MaxWidth = 190, MinHeight = 24, FontSize = 11, Padding = new Thickness(4, 2) };
                    ToolTip.SetTip(picker, option.Name);
                    var menu = new MenuFlyout();
                    foreach (var value in option.Values)
                    {
                        var item = new MenuItem { Header = value.Name, IsEnabled = value.Value != option.Current };
                        item.Click += async (_, _) => await Runtime(configured, owner).SetConfig(option, value.Value);
                        menu.Items.Add(item);
                    }
                    picker.Flyout = menu;
                    ConfigOptionsPanel.Children.Add(picker);
                }
        }
        WorkspaceSelectorButton.Content = (workspace?.Name ?? "Workspaces") + " ▾";
        Welcome.IsVisible = current is null;
        WelcomeHeading.Text = remoteOnly ? "Connect to your computer" : workspace is null ? "Open a folder to start" : "Start a chat in " + workspace.Name;
        WelcomeHint.Text = remoteOnly ? "Enter its address, then the pairing number shown on your desktop." : workspace is null ? "Your chats and terminals stay with the project." : "Choose Claude, Codex, or OpenCode from ＋ beside the workspace.";
        ComposerBorder.IsVisible = !remoteOnly;
        WelcomeOpenButton.IsVisible = workspace is null;
        MessageList.IsVisible = current is not null; HistoryNavigation.IsVisible = current is not null;
        ComposerBorder.IsEnabled = current is not null;
        ImportChatsButton.IsEnabled = workspace is not null;
        LoginButton.IsEnabled = workspace is not null;
        var provider = current?.Provider ?? AgentProvider.Codex;
        LoginButton.Content = provider == AgentProvider.OpenCode ? "Add provider" : "Log in to " + AgentProviders.Get(provider).Name;
        var login = loginSessions.GetValueOrDefault($"{workspace?.Id}:{provider}");
        LoginPanel.IsVisible = workspace is not null && (current?.NeedsLogin == true || authentication.GetValueOrDefault($"{workspace.Distro}:{provider}") || login.Control is not null);
        LoginHeading.Text = $"Sign in to {AgentProviders.Get(provider).Name} in {workspace?.Host} to continue this chat.";
        LoginTerminalHost.Content = login.Control;
        LoginTerminalHost.IsVisible = login.Control is not null;
        LoginButton.IsVisible = login.Control is null;
        LoginUrl.Text = login.Session?.Output.Link ?? "";
        LoginLinkPanel.IsVisible = login.Session?.Output.Link is not null;
        ChatHeading.Text = current?.Title ?? "Select a chat";
        ChatProviderIcon.Source = current is null ? null : BrandAssets.Provider(current.Provider);
        ToolTip.SetTip(ChatProviderIcon, current?.ProviderLabel);
        Composer.PlaceholderText = current is null ? "Message…" : $"Message {AgentProviders.Get(current.Provider).Name}…";
        SendButton.IsEnabled = current is not null && (current.Busy == false || runtimes.GetValueOrDefault(current.Id)?.IsPrompting == true) && runtimes.GetValueOrDefault(current.Id) is not { IsReconnecting: true } and not { IsConfiguring: true };
        SendButton.Label = current?.Busy == true ? "Queue message for after this turn (Enter)" : "Send (Enter)";
        ReconnectChatButton.IsEnabled = current is not null && runtimes.GetValueOrDefault(current.Id) is not { IsReconnecting: true } and not { IsLoadingHistory: true };
        StopButton.IsVisible = current is not null && runtimes.GetValueOrDefault(current.Id) is { } stopping && (stopping.IsPrompting || stopping.IsRecovering);
        ResumeChatButton.IsVisible = current?.InterruptedInput is not null && current.Busy == false && runtimes.GetValueOrDefault(current.Id) is not { IsRecovering: true };
        if (current is not null && runtimes.GetValueOrDefault(current.Id)?.IsRecovering == true) SendButton.IsEnabled = false;
        ArchiveChatButton.IsEnabled = current is not null;
        ArchiveChatButton.Label = current?.Archived == true ? "Restore chat" : "Archive chat";
        DeleteChatButton.IsEnabled = current is { Busy: false };
        StatusText.Text = current is null ? "Ready — open a workspace to begin" : $"{workspace?.Host}  ·  {current.Status}";
    }
    private async void SendClick(object? sender, RoutedEventArgs e) => await Send();
    private void CollapseOutputClick(object? sender, RoutedEventArgs e)
    {
        foreach (var view in MessageList.GetVisualDescendants().OfType<MessageView>()) view.Collapse();
    }
    private async void CopyChatClick(object? sender, RoutedEventArgs e)
    {
        if (current is null || Clipboard is null) return;
        var chat = current;
        try
        {
            foreach (var message in chat.Messages) store.SaveMessage(chat, message);
            var content = await store.ExportChatAsync(chat);
            await RichClipboard.Set(Clipboard, content.Plain, content.Html);
        }
        catch (Exception error) { StatusText.Text = "Could not copy chat: " + error.Message; }
    }
    private async Task Send()
    {
        if (current is not { } chat || workspace is null) return;
        var text = Composer.Text?.Trim() ?? ""; var attachments = chat.Attachments.ToArray();
        if (text.Length == 0 && attachments.Length == 0) return;
        if (chat.Title == "New chat") chat.Title = text.Length == 0 ? attachments[0].Name : text.Split('\n')[0][..Math.Min(70, text.Split('\n')[0].Length)];
        var runtime = Runtime(chat, workspace);
        if (runtime.IsReconnecting || runtime.IsConfiguring) return;
        if (chat.Busy)
        {
            if (!runtime.IsPrompting) return;
            runtime.Queue(new(text, attachments)); Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear(); store.Save(chat); UpdateControls(); return;
        }
        Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear();
        await runtime.Send(text, attachments);
        if (ReferenceEquals(chat, current)) Composer.Text = chat.Draft;
        UpdateControls();
    }
    private ChatRuntime Runtime(Chat chat, Workspace owner)
    {
        if (!runtimes.TryGetValue(chat.Id, out var runtime))
        {
            runtime = new(chat, owner, store, AgentProviders.Command(store, owner, chat.Provider));
            runtime.Permission = (request, token) => Permission(chat, request, token);
            runtime.Changed += () =>
            {
                if (closing) return;
                store.TrimHistory(chat);
                if ((!ReferenceEquals(chat, current) || uiSleeping || remoteView is not null) && !chat.RetainHistory) store.ReleaseHistory(chat);
                if (!ReferenceEquals(chat, current) || uiSleeping || remoteView is not null) { UpdateTray(); return; }
                if (ReferenceEquals(chat, current) && runtime.IsRecovering && Composer.Text != chat.Draft) Composer.Text = chat.Draft;
                UpdateControls();
                if (uiSleeping) return;
                if (ReferenceEquals(chat, current) && remoteView is null && !viewingHistory)
                {
                    if (runtime.IsLoadingHistory)
                    {
                        // Replay can replace hundreds of rows a second. Keep the visible
                        // page stable until replay finishes instead of laying out each chunk.
                        if (ReferenceEquals(MessageList.ItemsSource, chat.Messages)) MessageList.ItemsSource = chat.Messages.ToArray();
                    }
                    else if (!ReferenceEquals(MessageList.ItemsSource, chat.Messages))
                    {
                        MessageList.ItemsSource = chat.Messages;
                        ScrollTranscriptToEnd();
                    }
                    else if (TranscriptScroll.Offset.Y + TranscriptScroll.Viewport.Height >= TranscriptScroll.Extent.Height - 150)
                        ScrollTranscriptToEnd();
                }
            };
            runtimes[chat.Id] = runtime;
        }
        return runtime;
    }
    private async void ReconnectChatClick(object? sender, RoutedEventArgs e)
    {
        if (current is not { } chat || workspace is null) return;
        await Runtime(chat, workspace).Reconnect();
        if (ReferenceEquals(current, chat)) Composer.Text = chat.Draft;
        UpdateControls();
    }
    private async void StopClick(object? sender, RoutedEventArgs e)
    { try { if (current is not null && runtimes.TryGetValue(current.Id, out var runtime)) await runtime.Stop(); } catch (OperationCanceledException) { } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void ComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (SlashCommands.IsVisible && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.Enter or Key.Tab) { e.Handled = true; InsertSlashCommand(); return; }
            if (e.Key is Key.Up or Key.Down) { e.Handled = true; SlashCommands.SelectedIndex = Math.Clamp(SlashCommands.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, SlashCommands.ItemCount - 1); SlashCommands.ScrollIntoView(SlashCommands.SelectedItem!); return; }
            if (e.Key == Key.Escape) { e.Handled = true; SlashCommands.IsVisible = false; return; }
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; await Send(); }
        else if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            await PasteClipboard(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (e.Key == Key.Escape && current?.Busy == true)
        {
            e.Handled = true;
            var twice = ReferenceEquals(escapeChat, current) && DateTimeOffset.UtcNow - lastEscape < TimeSpan.FromMilliseconds(650);
            lastEscape = DateTimeOffset.UtcNow; escapeChat = current;
            if (twice) { lastEscape = default; StopClick(this, new()); }
            else { Composer.Focus(); await SteerDraft(); }
        }
    }
    private async void AttachFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Reference files or images", AllowMultiple = true });
        await AddFiles(files);
    }
    private async void DropFiles(object? sender, DragEventArgs e)
    { e.Handled = true; await AddFiles(e.DataTransfer.TryGetFiles() ?? []); }
    private async Task AddFiles(IEnumerable<IStorageItem> files, Chat? target = null)
    {
        target ??= current; if (target is null) return;
        foreach (var file in files)
        {
            try
            {
                var path = file.TryGetLocalPath(); if (path is null) continue;
                if (Directory.Exists(path)) throw new IOException("Drop individual files, or use Open workspace for a folder.");
                if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new IOException("Attachments must be smaller than 20 MB.");
                var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var mime = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "text/plain" };
                var data = mime.StartsWith("image/") ? Convert.ToBase64String(await File.ReadAllBytesAsync(path)) : await File.ReadAllTextAsync(path);
                if (data.Contains('\0')) throw new IOException("This binary file cannot be included as text.");
                target.Attachments.Add(new(file.Name, mime, data, path));
            }
            catch (Exception ex) { StatusText.Text = file.Name + ": " + ex.Message; }
        }
    }
    private void RemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Attachment attachment } || current is null) return;
        current.Attachments.Remove(attachment);
        if (attachment.Reference is { } reference) Composer.Text = (Composer.Text ?? "").Replace(reference, "", StringComparison.Ordinal);
    }
    private async Task PasteClipboard(bool textOnly)
    {
        if (current is not { } target || Clipboard is null) return;
        var draft = Composer.Text ?? "";
        var start = Math.Min(Composer.SelectionStart, Composer.SelectionEnd);
        var end = Math.Max(Composer.SelectionStart, Composer.SelectionEnd);
        try
        {
            using var data = await Clipboard.TryGetDataAsync();
            if (data is null) return;
            if (!textOnly)
            {
                using var bitmap = await data.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    using var stream = new MemoryStream(); bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    if (stream.Length > 20 * 1024 * 1024) throw new IOException("Attachments must be smaller than 20 MB.");
                    var index = 1;
                    while (target.Attachments.Any(a => a.Reference == $"[Image #{index}]")) index++;
                    var reference = $"[Image #{index}]";
                    target.Attachments.Add(new($"Pasted image {index}.png", "image/png", Convert.ToBase64String(stream.ToArray()), Reference: reference));
                    InsertPaste(target, draft, start, end, reference);
                    return;
                }
                var files = await data.TryGetFilesAsync();
                if (files?.Any() == true) { await AddFiles(files, target); return; }
            }
            var text = await data.TryGetTextAsync();
            // Browser/desktop link drags sometimes publish only a URI list.
            text ??= await data.TryGetValueAsync(DataFormat.CreateStringPlatformFormat("text/uri-list"));
            if (text is not null) InsertPaste(target, draft, start, end, text);
        }
        catch (Exception error) { StatusText.Text = "Paste failed: " + error.Message; }
    }
    private void InsertPaste(Chat target, string draft, int start, int end, string text)
    {
        // Clipboard access is asynchronous: don't insert into a newly selected chat
        // or overwrite text typed while the operating system was supplying the data.
        var updated = target.Draft == draft ? draft[..start] + text + draft[end..] : target.Draft + text;
        target.Draft = updated;
        if (ReferenceEquals(current, target))
        {
            Composer.Text = updated;
            Composer.CaretIndex = updated.Length == draft.Length - (end - start) + text.Length ? start + text.Length : updated.Length;
            Composer.SelectionStart = Composer.SelectionEnd = Composer.CaretIndex;
        }
        store.Save(target);
    }
    private async Task<JsonObject> Permission(Chat chat, JsonElement request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remotePermissionId = remoteSessions.RegisterPermission(chat, request, completion);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) { completion.TrySetResult(RpcJson.Permission()); return; }
            if (!IsVisible) ShowFromTray();
            chat.Status = "Needs permission"; UpdateControls();
            var dialog = new Window { Title = "Codex permission · " + chat.Title, Width = 650, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var option in request.GetProperty("options").EnumerateArray())
            {
                var id = option.GetProperty("optionId").GetString();
                var button = new Button { Content = option.GetProperty("name").GetString(), Margin = new Thickness(4) };
                button.Click += (_, _) => { completion.TrySetResult(RpcJson.Permission(id)); dialog.Close(); };
                buttons.Children.Add(button);
            }
            dialog.Content = new DockPanel { Margin = new Thickness(22), Children = { buttons, new ScrollViewer { Content = new SelectableTextBlock { Text = request.GetProperty("toolCall").ToString(), TextWrapping = TextWrapping.Wrap } } } };
            DockPanel.SetDock(buttons, Dock.Bottom);
            dialog.Closed += (_, _) => completion.TrySetResult(RpcJson.Permission());
            _ = completion.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() => { remoteSessions.ForgetPermission(remotePermissionId); dialog.Close(); }));
            var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
            dialog.Closed += (_, _) => registration.Dispose();
            _ = dialog.ShowDialog(desktopWindow!);
        });
        return await completion.Task;
    }
    private async void ImportChatsClick(object? sender, RoutedEventArgs e)
    {
        if (workspace is not { } owner) return;
        var dialog = new Window { Title = "Import chats", Width = 400, Height = 290, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var options = AgentProviders.All.Select(p => new CheckBox { Content = p.Label, Tag = p.Provider, IsChecked = p.Provider != AgentProvider.Codex }).ToArray();
        var import = new Button { Name = "ConfirmImportButton", Content = "Import", HorizontalAlignment = HorizontalAlignment.Right, Classes = { "accent" } };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Import previous chats for " + owner.Name, TextWrapping = TextWrapping.Wrap });
        foreach (var option in options) panel.Children.Add(option);
        panel.Children.Add(new TextBlock { Text = "Uses each agent’s history in " + owner.Host + ". Existing chats are skipped.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        panel.Children.Add(import); dialog.Content = panel;
        import.Click += (_, _) => dialog.Close(options.Where(p => p.IsChecked == true).Select(p => (AgentProvider)p.Tag!).ToArray());
        var providers = await dialog.ShowDialog<AgentProvider[]?>(desktopWindow!);
        if (providers is null || closing) return;
        var tasks = providers.Select(provider =>
        {
            var key = owner.Id + ":" + provider;
            if (discoveries.TryGetValue(key, out var pending) && pending is Task<string> existing && !pending.IsCompleted) return existing;
            var task = DiscoverHistory(owner, provider); discoveries[key] = task; return task;
        }).ToArray();
        var reports = await Task.WhenAll(tasks);
        if (!closing && workspace?.Id == owner.Id)
        {
            StatusText.Text = string.Join(" · ", reports.Where(r => r.Length > 0));
            ToolTip.SetTip(StatusText, StatusText.Text);
        }
    }
    private async void SettingsClick(object? sender, RoutedEventArgs e)
    {
        if (remoteOnly) { ShowConnectionSettings(); return; }
        var dialog = new Window { Title = "Settings", Width = 680, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        var sleep = new CheckBox { Name = "PresentationSleep", Content = "Sleep UI when hidden or inactive (after 2 seconds)", IsChecked = store.Setting("presentationSleep") == "1" };
        sleep.IsCheckedChanged += (_, _) => { store.Setting("presentationSleep", sleep.IsChecked == true ? "1" : "0"); SchedulePresentationSleep(); };
        panel.Children.Add(sleep);
        var syntax = new CheckBox { Name = "SyntaxHighlighting", Content = "Syntax highlighting in code blocks", IsChecked = store.Setting("syntaxHighlighting") != "0" };
        syntax.IsCheckedChanged += (_, _) => { var enabled = syntax.IsChecked == true; store.Setting("syntaxHighlighting", enabled ? "1" : "0"); AppTheme.SetSyntaxHighlighting(enabled); };
        panel.Children.Add(syntax);
        var traySetting = new CheckBox { Name = "RunInTray", Content = "Keep agents running in the system tray when the window closes", IsChecked = store.Setting("runInTray") != "0" };
        traySetting.IsCheckedChanged += (_, _) => { store.Setting("runInTray", traySetting.IsChecked == true ? "1" : "0"); ConfigureTray(); };
        panel.Children.Add(traySetting);
        var autoResume = new CheckBox { Name = "AutoResumeInterrupted", Content = "Automatically resume interrupted chats when the app starts", IsChecked = store.Setting("autoResume") == "1" };
        autoResume.IsCheckedChanged += (_, _) => store.Setting("autoResume", autoResume.IsChecked == true ? "1" : "0");
        var startAtLogin = new CheckBox { Name = "StartAtLogin", Content = "Start Vibe Harder when I sign in (including after a restart)", IsChecked = store.Setting("startAtLogin") == "1" };
        panel.Children.Add(autoResume); panel.Children.Add(startAtLogin);
        panel.Children.Add(new TextBlock { Text = "Enable both for unattended recovery after a restart. Explicitly stopped chats stay stopped. Agents still use their existing permission settings.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var terminalSettings = new Button { Content = "Terminal settings…" };
        terminalSettings.Click += async (_, _) => await new TerminalSettings(store, store.Workspaces()).ShowDialog(dialog);
        panel.Children.Add(terminalSettings);
        var fontSettings = new Button { Content = "Fonts…" };
        fontSettings.Click += async (_, _) => await new FontSettings(store).ShowDialog(dialog);
        panel.Children.Add(fontSettings);
        var remote = new Button { Content = "Remote hosts and server…" };
        remote.Click += async (_, _) => { var host = await new RemoteSettings(store, remoteServer?.Fingerprint, ConfigureRemoteServer).ShowDialog<RemoteHost?>(dialog); BuildWorkspaceTree(); if (host is not null) { dialog.Close(); OpenRemoteHost(host); } };
        panel.Children.Add(remote);
        panel.Children.Add(new TextBlock { Text = "Color theme" }); panel.Children.Add(AppTheme.Picker(store));
        var fields = new Dictionary<string, TextBox>();
        foreach (var provider in AgentProviders.All)
        {
            panel.Children.Add(new TextBlock { Text = provider.Label, FontWeight = FontWeight.SemiBold });
            foreach (var isWsl in new[] { false, true })
            {
                var key = AgentProviders.CommandKey(provider.Provider, isWsl);
                var field = new TextBox { Text = store.Setting(key) ?? provider.DefaultCommand, TextWrapping = TextWrapping.Wrap };
                fields[key] = field;
                var loginKey = $"{provider.Provider}:{(isWsl ? "wsl" : "local")}LoginCommand";
                var loginField = new TextBox { Text = AgentProviders.LoginCommand(store, new Workspace("settings", "", "/", isWsl ? "WSL" : null), provider.Provider), TextWrapping = TextWrapping.Wrap };
                fields[loginKey] = loginField;
                panel.Children.Add(new TextBlock { Text = isWsl ? "WSL command" : "Local command", Classes = { "muted" } }); panel.Children.Add(field);
                panel.Children.Add(new TextBlock { Text = "Account setup command", Classes = { "muted" } }); panel.Children.Add(loginField);
            }
        }
        foreach (var key in new[] { "localCodexCommand", "wslCodexCommand" })
        {
            panel.Children.Add(new TextBlock { Text = key.StartsWith("wsl") ? "WSL Codex deletion command" : "Local Codex deletion command", Classes = { "muted" } });
            var field = new TextBox { Text = store.Setting(key) ?? ChatHistory.DefaultCodexCommand }; fields[key] = field; panel.Children.Add(field);
        }
        panel.Children.Add(new TextBlock { Text = "Sign in to each agent in its own environment. OpenCode uses opencode acp; Claude uses the Claude Agent SDK adapter. Commands apply to new connections and imports.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var save = new Button { Content = "Save settings", Classes = { "accent" } }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            if (fields.Values.Any(f => string.IsNullOrWhiteSpace(f.Text))) return;
            try { if ((startAtLogin.IsChecked == true) != (store.Setting("startAtLogin") == "1")) StartupRegistration.SetEnabled(startAtLogin.IsChecked == true); }
            catch (Exception error) { StatusText.Text = "Could not update startup registration: " + error.Message; return; }
            foreach (var (key, field) in fields) store.Setting(key, field.Text!);
            store.Setting("autoResume", autoResume.IsChecked == true ? "1" : "0"); store.Setting("startAtLogin", startAtLogin.IsChecked == true ? "1" : "0");
            store.Setting("runInTray", traySetting.IsChecked == true ? "1" : "0"); ConfigureTray(); dialog.Close();
        };
        dialog.Content = new ScrollViewer { Content = panel }; await dialog.ShowDialog(desktopWindow!);
    }
    private async void PaletteClick(object? sender, RoutedEventArgs e)
    {
        if (remoteOnly) return;
        if (palette is not null) { palette.Activate(); return; }
        var owner = workspace;
        List<PaletteCommand> commands = [
            new("Open workspace", "Choose a local or WSL folder", () => { OpenWorkspaceClick(this, new()); return Task.CompletedTask; }),
            new("Connection settings", "Configure agent and account commands", () => { SettingsClick(this, new()); return Task.CompletedTask; })
        ];
        commands.Add(new("Terminal settings", "Choose a shell for each platform", async () => await new TerminalSettings(store, store.Workspaces()).ShowDialog(desktopWindow!)));
        if (owner is not null)
        {
            commands.Add(new("New terminal", owner.Caption, () => NewTerminal(target: owner)));
            commands.Add(new("Import previous chats", owner.Caption, () => { ImportChatsClick(this, new()); return Task.CompletedTask; }));
            foreach (var provider in AgentProviders.All)
            {
                commands.Add(new("New " + provider.Name + " chat", provider.Label, () => { SelectWorkspace(owner); NewChat(provider.Provider); return Task.CompletedTask; }));
                commands.Add(new(provider.Provider == AgentProvider.OpenCode ? "OpenCode: add provider" : provider.Name + ": log in", owner.Host + " terminal", () => Login(owner, provider.Provider)));
            }
            if (current is { } chat) commands.Add(new("Reconnect current chat", chat.ProviderLabel, () => Runtime(chat, owner).Reconnect()));
        }
        palette = new CommandPalette(commands, owner);
        try
        {
            var command = await palette.ShowDialog<PaletteCommand?>(desktopWindow!);
            palette = null;
            if (command is not null && !closing) await command.Execute();
        }
        catch (Exception error) { StatusText.Text = "Command failed: " + error.Message; }
        finally { palette = null; }
    }
    private async void LoginClick(object? sender, RoutedEventArgs e)
    {
        if (workspace is not { } owner) return;
        await Login(owner, current?.Provider ?? AgentProvider.Codex);
    }
    private async void ResumeChatClick(object? sender, RoutedEventArgs e)
    { if (current is { InterruptedInput: { } input } chat && workspace is { } owner) await ResumeInterrupted(chat, owner, input); }
    private async void CheckLoginClick(object? sender, RoutedEventArgs e)
    {
        if (workspace is not { } owner) return;
        var provider = current?.Provider ?? AgentProvider.Codex;
        if (loginSessions.Remove($"{owner.Id}:{provider}", out var login)) login.Session.Dispose();
        CheckLoginButton.IsEnabled = false;
        try { await AfterLogin(owner, provider); }
        finally { CheckLoginButton.IsEnabled = true; UpdateControls(); }
    }
    private async void OpenLoginLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(LoginUrl.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            await Launcher.LaunchUriAsync(uri);
    }
    private async void CopyLoginLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && !string.IsNullOrEmpty(LoginUrl.Text)) await Clipboard.SetTextAsync(LoginUrl.Text);
    }
    private TerminalControl CreateTerminalControl(TerminalSession session, int fontSize)
    {
        var control = new ThemedTerminalControl { Model = session.Model, FontSize = fontSize, FontFamily = new FontFamily("avares://VibeHarder.UI/Assets/Fonts#NeoSpleen Nerd Font") };
        control.Bind(TerminalControl.FontFamilyProperty, this.GetResourceObservable("TerminalFont"));
        control.Bind(TerminalControl.FontSizeProperty, this.GetResourceObservable("TerminalFontSize"));
        control.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (!(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
            if (e.Key == Key.V) { e.Handled = true; await control.PasteFromClipboardAsync(); }
            else if (e.Key == Key.C && (control.HasSelection || e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            { e.Handled = true; await control.CopySelectionAsync(); }
        }, RoutingStrategies.Tunnel);
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += async (_, _) => await control.CopySelectionAsync();
        var paste = new MenuItem { Header = "Paste" };
        paste.Click += async (_, _) => await control.PasteFromClipboardAsync();
        var select = new MenuItem { Header = "Select all" };
        select.Click += (_, _) => control.SelectAll();
        control.ContextMenu = new ContextMenu { ItemsSource = new[] { copy, paste, select } };
        return control;
    }
    private async Task Login(Workspace owner, AgentProvider provider)
    {
        var key = $"{owner.Id}:{provider}";
        if (loginSessions.ContainsKey(key)) { UpdateControls(); loginSessions[key].Control.Focus(); return; }
        var session = new TerminalSession();
        var control = CreateTerminalControl(session, 12);
        session.OutputChanged += () => { if (!closing) UpdateControls(); };
        loginSessions[key] = (control, session);
        session.Completed += () =>
        {
            loginSessions.Remove(key); session.Dispose();
            if (!closing) discoveries["login:" + key] = AfterLogin(owner, provider);
        };
        UpdateControls();
        try { await session.Start(owner, AgentProviders.LoginCommand(store, owner, provider)); control.Focus(); }
        catch (Exception error) { loginSessions.Remove(key); session.Dispose(); UpdateControls(); StatusText.Text = "Login failed: " + error.Message; }
    }
    private async Task AfterLogin(Workspace owner, AgentProvider provider)
    {
        if (closing) return;
        authentication[$"{owner.Distro}:{provider}"] = false;
        foreach (var chat in chats.Where(c => c.Provider == provider && workspaces.Any(w => w.Id == c.WorkspaceId && w.Distro == owner.Distro)).ToArray())
        {
            chat.NeedsLogin = false;
            if (runtimes.TryGetValue(chat.Id, out var runtime) && !chat.Busy) await runtime.Reconnect();
            if (closing) return;
        }
        await DiscoverHistory(owner, provider); if (!closing) UpdateControls();
    }
    private async void ToggleTerminal(object? sender, RoutedEventArgs e)
    {
        if (remoteOnly) return;
        TerminalDrawer.IsVisible = !TerminalDrawer.IsVisible;
        if (!TerminalDrawer.IsVisible) { Composer.Focus(); return; }
        if (TerminalTabs.ItemCount == 0) await NewTerminal();
        else if (TerminalTabs.SelectedItem is TabItem { Content: Control terminal }) terminal.Focus();
    }
    private async void NewTerminalClick(object? sender, RoutedEventArgs e) => await NewTerminal();
    private async Task<TabItem?> NewTerminal(string? command = null, string? title = null, Workspace? target = null, Action? completed = null)
    {
        if (remoteOnly) return null;
        var owner = target ?? workspace;
        if (owner is null) return null;
        var session = new TerminalSession();
        if (completed is not null) session.Completed += completed;
        var control = CreateTerminalControl(session, 13);
        var tab = new TabItem { Content = control, MinHeight = 26, Padding = new(6, 2) };
        var close = new Button { Content = "×", Padding = new Thickness(3, 0), MinHeight = 18, Height = 18 };
        var list = terminals.GetValueOrDefault(owner.Id);
        if (list is null) terminals[owner.Id] = list = [];
        tab.Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { FontSize = 11, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, Text = title ?? $"{owner.Host} {list.Count + 1}", VerticalAlignment = VerticalAlignment.Center }, close } };
        list.Add((tab, session));
        close.Click += (_, _) => { session.Dispose(); list.RemoveAll(t => t.Tab == tab); TerminalTabs.ItemsSource = list.Select(t => t.Tab).ToArray(); if (list.Count > 0) TerminalTabs.SelectedIndex = 0; };
        TerminalTabs.ItemsSource = list.Select(t => t.Tab).ToArray(); TerminalTabs.SelectedItem = tab; TerminalDrawer.IsVisible = true;
        try { await session.Start(owner, command, store); control.Focus(); return tab; } catch (Exception error) { session.Dispose(); session.Model.Feed("Could not start terminal: " + error.Message); StatusText.Text = error.Message; return null; }
    }
    private void TerminalResizeStart(object? sender, PointerPressedEventArgs e) { resizeY = e.GetPosition(this).X; resizeHeight = TerminalDrawer.Width; e.Pointer.Capture(sender as IInputElement); }
    private void TerminalResizeMove(object? sender, PointerEventArgs e) { if (resizeY is { } y) TerminalDrawer.Width = Math.Clamp(resizeHeight + y - e.GetPosition(this).X, 220, Math.Max(220, Bounds.Width - 650)); }
    private void TerminalResizeEnd(object? sender, PointerReleasedEventArgs e) { resizeY = null; e.Pointer.Capture(null); }
    private void SaveAll()
    { store.Setting("sidebarWidth", (!compact && sidebarOpen ? RootPanes.ColumnDefinitions[0].ActualWidth : sidebarWidth).ToString(System.Globalization.CultureInfo.InvariantCulture)); store.Setting("terminalWidth", TerminalDrawer.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)); foreach (var c in chats) { if (runtimes.GetValueOrDefault(c.Id)?.IsLoadingHistory == true) continue; store.Save(c); foreach (var m in c.Messages) store.SaveMessage(c, m); } }
    private async Task BrowseHistory(bool newer)
    {
        if (current is not { } chat) return;
        pageLoad?.Cancel(); var cancellation = pageLoad = CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        var visible = MessageList.Items.OfType<Message>().ToArray(); if (visible.Length == 0) return;
        try
        {
            var page = await store.ReadPageAsync(chat, newer ? visible[^1].Sequence : visible[0].Sequence, token: cancellation.Token, newer: newer);
            if (cancellation.IsCancellationRequested || current != chat || page.Length == 0) return;
            viewingHistory = true; MessageList.ItemsSource = page; MessageList.ScrollIntoView(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Could not load history: " + error.Message; }
    }
    private async void EarlierMessagesClick(object? sender, RoutedEventArgs e) => await BrowseHistory(false);
    private async void NewerMessagesClick(object? sender, RoutedEventArgs e) => await BrowseHistory(true);
    private async void LatestMessagesClick(object? sender, RoutedEventArgs e)
    {
        pageLoad?.Cancel(); viewingHistory = false; MessageList.ItemsSource = current?.Messages;
        try { if (current is { HistoryLoaded: false } chat) await RestoreVisibleHistory(chat, discoveryLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Could not reload history: " + error.Message; }
        ScrollTranscriptToEnd();
    }
    private bool transcriptScrollPending;
    private void ScrollTranscriptToEnd()
    {
        if (uiSleeping || transcriptScrollPending) return;
        transcriptScrollPending = true;
        var chat = current;
        Dispatcher.UIThread.Post(() =>
        {
            transcriptScrollPending = false;
            if (closing || viewingHistory || !ReferenceEquals(current, chat) ||
                (chat is not null && runtimes.TryGetValue(chat.Id, out var runtime) && runtime.IsLoadingHistory)) return;
            MessageList.ScrollIntoView(MessageList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
    private void ClearChat()
    {
        if (current is not null && chats.Contains(current)) { current.Draft = Composer.Text ?? ""; store.Save(current); }
        if (current is not null) DeferHistoryEviction(current);
        current = null; Composer.Text = ""; MessageList.ItemsSource = null; AttachmentList.ItemsSource = null; UpdateControls();
    }
    private void SelectNextChat()
    {
        ClearChat(); RefreshChats(); ChatList.SelectedItem = ChatList.Items.FirstOrDefault();
    }
    private void ToggleArchiveView(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var archived in new[] { false, true })
        {
            var item = new MenuItem { Header = archived ? "Archived" : "Chats", IsEnabled = archived != showArchived };
            item.Click += (_, _) => { showArchived = archived; ArchiveViewButton.Content = archived ? "Archived ▾" : "Chats ▾"; SearchBox.Text = ""; SelectNextChat(); };
            menu.Items.Add(item);
        }
        ArchiveViewButton.Flyout = menu; menu.ShowAt(ArchiveViewButton);
    }
    private async void ArchiveChatClick(object? sender, RoutedEventArgs e)
    {
        if (current is { } chat) await ArchiveChat(chat);
    }
    private async Task ArchiveChat(Chat chat)
    {
        if (ReferenceEquals(current, chat)) pageLoad?.Cancel();
        chat.Archived = !chat.Archived; store.Save(chat);
        Task? cleanup = null;
        if (chat.Archived && runtimes.Remove(chat.Id, out var runtime)) cleanup = runtime.DisposeAsync().AsTask();
        if (ReferenceEquals(current, chat)) SelectNextChat(); else { RefreshChats(); UpdateControls(); }
        if (cleanup is not null) { workspaceClosures.Add(cleanup); try { await cleanup; } finally { workspaceClosures.Remove(cleanup); } }
        if (chat.Archived) { chat.Messages.Clear(); chat.HistoryLoaded = false; chat.InterruptedInput = null; store.Setting("interrupted:" + chat.Id, ""); store.Save(chat); }
    }
    private async void DeleteChatClick(object? sender, RoutedEventArgs e)
    {
        if (current is not { Busy: false } chat || workspace is null) return;
        var owner = workspace;
        var codexHistory = chat.Provider == AgentProvider.Codex;
        var dialog = new Window { Title = codexHistory ? "Delete chat permanently" : "Remove chat", Width = 530, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var confirm = new Button { Name = "ConfirmDeleteButton", Content = codexHistory ? "Delete permanently" : "Remove from app" }; var cancel = new Button { Content = "Cancel" };
        confirm.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = { new TextBlock { Text = "Delete “" + chat.Title + "”?", FontSize = 20, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = codexHistory ? "This removes the chat and attachments from this app and permanently deletes its Codex history, including any child sessions. Project files are kept." : "Remove this chat and its attachments from this app? The original agent history is kept. This chat will be skipped by future imports.", TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } } } };
        if (!await dialog.ShowDialog<bool>(desktopWindow!)) return;
        historyOperation = DeleteChat(chat, owner); await historyOperation;
    }
    private async Task DeleteChat(Chat chat, Workspace owner)
    {
        chat.Busy = true; chat.Status = "Deleting…"; UpdateControls();
        try
        {
            if (runtimes.Remove(chat.Id, out var runtime)) await runtime.DisposeAsync();
            if (chat.SessionId is not null && chat.Provider == AgentProvider.Codex) await ChatHistory.DeleteFromCodex(owner, chat.SessionId, store.Setting(owner.IsWsl ? "wslCodexCommand" : "localCodexCommand"));
            if (chat.SessionId is not null) store.Setting(AgentProviders.HiddenHistoryKey(chat), "1");
            store.Delete(chat); chats.Remove(chat); if (ReferenceEquals(current, chat)) SelectNextChat(); StatusText.Text = chat.Provider == AgentProvider.Codex ? "Chat and Codex history deleted." : "Chat removed from this app.";
        }
        catch (Exception error) { chat.Status = "Delete failed"; StatusText.Text = "History deletion failed; the chat was kept. " + error.Message; }
        finally { chat.Busy = false; var status = StatusText.Text; UpdateControls(); StatusText.Text = status; }
    }
    private bool TrayAvailable => tray is not null && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || tray.NativeMenuExporter is not null);
    private void ConfigureTray()
    {
        tray?.Dispose(); tray = null;
        if (remoteOnly || desktopWindow is null || store.Setting("runInTray") == "0") return;
        var show = new NativeMenuItem("Open Vibe Harder"); show.Click += (_, _) => ShowFromTray();
        var quit = new NativeMenuItem("Quit and interrupt agents"); quit.Click += (_, _) => RequestExit();
        tray = new TrayIcon { Icon = desktopWindow.Icon, ToolTipText = "Vibe Harder — no agents running", IsVisible = true, Menu = new NativeMenu { Items = { show, quit } } };
        UpdateTray();
        tray.Clicked += (_, _) => ShowFromTray();
        if (Application.Current is { } app) TrayIcon.SetIcons(app, new TrayIcons { tray });
    }
    private string trayState = "";
    private void UpdateTray()
    {
        if (tray is null) return;
        var active = chats.Where(c => runtimes.TryGetValue(c.Id, out var runtime) && runtime.IsPrompting).ToArray();
        var signature = string.Join("|", active.Select(c => c.Id + c.Title + c.Status));
        if (signature == trayState && tray.Menu?.Items.Count > 2) return;
        trayState = signature;
        tray.ToolTipText = active.Length == 0 ? "Vibe Harder — no agents running" : $"Vibe Harder — {active.Length} agent{(active.Length == 1 ? "" : "s")} running";
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Open Vibe Harder"); show.Click += (_, _) => ShowFromTray(); menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        if (active.Length == 0) menu.Items.Add(new NativeMenuItem("No agents running") { IsEnabled = false });
        foreach (var chat in active)
        {
            var owner = workspaces.FirstOrDefault(w => w.Id == chat.WorkspaceId);
            var item = new NativeMenuItem($"{AgentProviders.Get(chat.Provider).Name} · {owner?.Name} · {chat.Title} · {chat.Status}");
            item.Click += (_, _) => { ShowFromTray(); if (owner is not null) { SelectWorkspace(owner); ChatList.SelectedItem = chat; } }; menu.Items.Add(item);
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem(active.Length == 0 ? "Quit" : "Quit and interrupt agents"); quit.Click += (_, _) => RequestExit(); menu.Items.Add(quit);
        tray.Menu = menu;
    }
    public void ShowFromTray() { desktopWindow?.Show(); if (desktopWindow is { } window) { window.WindowState = WindowState.Normal; window.Activate(); } UpdateControls(); }
    public void RequestExit() { exitRequested = true; desktopWindow?.Close(); }
    private async Task OfferInterruptedChats()
    {
        if (recoveryOffered) return; recoveryOffered = true;
        var interrupted = chats.Where(c => c.InterruptedInput is not null).ToArray();
        if (interrupted.Length == 0 || closing) return;
        if (store.Setting("autoResume") == "1")
        {
            foreach (var chat in interrupted)
            {
                var owner = store.Workspaces().FirstOrDefault(w => w.Id == chat.WorkspaceId);
                if (owner is null) continue;
                if (!workspaces.Any(w => w.Id == owner.Id)) { workspaces.Add(owner); store.Setting("closed:" + owner.Id, "0"); BuildWorkspaceTree(); }
                _ = ResumeInterrupted(chat, owner, chat.InterruptedInput!);
            }
            return;
        }
        var dialog = new Window { Title = "Resume interrupted chats", Width = 520, Height = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "These chats were interrupted when the app exited. Resume selected chats from their saved history? Agents will check existing progress before continuing.", TextWrapping = TextWrapping.Wrap });
        var checks = interrupted.Select(c => (Chat: c, Check: new CheckBox { Content = c.Title, IsChecked = true })).ToArray();
        foreach (var item in checks) panel.Children.Add(item.Check);
        var resume = new Button { Name = "ResumeInterrupted", Content = "Resume selected chats" };
        var keep = new Button { Name = "KeepInterruptedDrafts", Content = "Keep drafts without resuming" };
        panel.Children.Add(resume); panel.Children.Add(keep); dialog.Content = new ScrollViewer { Content = panel };
        keep.Click += (_, _) => { foreach (var chat in interrupted) { chat.InterruptedInput = null; store.Setting("interrupted:" + chat.Id, ""); } dialog.Close(); };
        resume.Click += (_, _) =>
        {
            dialog.Close();
            foreach (var (chat, check) in checks.Where(c => c.Check.IsChecked == true))
            {
                var input = chat.InterruptedInput!;
                var owner = store.Workspaces().FirstOrDefault(w => w.Id == chat.WorkspaceId);
                if (owner is null) continue;
                if (!workspaces.Any(w => w.Id == owner.Id)) { workspaces.Add(owner); store.Setting("closed:" + owner.Id, "0"); BuildWorkspaceTree(); }
                _ = ResumeInterrupted(chat, owner, input);
            }
        };
        await dialog.ShowDialog(desktopWindow!);
    }
    private async Task ResumeInterrupted(Chat chat, Workspace owner, PendingInput input)
    {
        var runtime = Runtime(chat, owner);
        while (runtime.IsReconnecting || runtime.IsLoadingHistory) { if (closing) return; await Task.Delay(50); }
        if (chat.Busy || closing) return;
        while (!runtime.IsConnected && !closing)
        {
            await runtime.Reconnect();
            if (closing) return;
            if (runtime.IsConnected) break;
            if (chat.NeedsLogin || store.Setting("autoResume") != "1") return;
            chat.Status = "Waiting to reconnect before automatic resume…"; UpdateControls();
            try { await Task.Delay(TimeSpan.FromSeconds(15), discoveryLifetime.Token); } catch (OperationCanceledException) { return; }
        }
        if (closing || chat.Busy) return;
        chat.InterruptedInput = null;
        if (chat.Draft == input.Text) { chat.Draft = ""; if (ReferenceEquals(current, chat)) Composer.Text = ""; }
        foreach (var attachment in input.Attachments) chat.Attachments.Remove(attachment);
        await runtime.Send("Continue the interrupted request below. First inspect the saved conversation and current workspace state; do not repeat actions already completed.\n\n" + input.Text, input.Attachments);
    }
    private bool shutdownComplete;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!closing && !exitRequested && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined && store.Setting("runInTray") != "0" && TrayAvailable)
        { e.Cancel = true; SaveAll(); desktopWindow?.Hide(); return; }
        if (closing) { e.Cancel = !shutdownComplete; return; }
        e.Cancel = true; closing = true; DisposePresentationSleep(); discoveryLifetime.Cancel(); saveTimer.Stop();
        var errors = new List<Exception>();
        async Task Cleanup(Func<Task> action)
        {
            try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { errors.Add(error); }
        }
        try
        {
            await Cleanup(() => { SaveAll(); return Task.CompletedTask; });
            CloseRemoteView();
            // Cancel providers before waiting for operations that depend on them.
            var stoppingAgents = runtimes.Values.Select(runtime => Cleanup(() => runtime.DisposeAsync().AsTask())).ToArray();
            foreach (var login in loginSessions.Values) await Cleanup(() => Task.Run(login.Session.Dispose));
            foreach (var terminal in terminals.Values.SelectMany(t => t)) await Cleanup(() => Task.Run(terminal.Session.Dispose));
            if (remoteServer is not null) await Cleanup(() => remoteServer.DisposeAsync().AsTask());
            await Task.WhenAll(stoppingAgents);
            await Cleanup(() => Task.WhenAll(discoveries.Values));
            await Cleanup(() => Task.WhenAll(workspaceClosures.ToArray()));
            if (historyOperation is not null) await Cleanup(() => historyOperation);
            await Cleanup(async () => { SaveAll(); await store.FlushAsync(); });
            await Cleanup(() => Task.Run(store.Dispose));
        }
        finally
        {
            tray?.Dispose(); tray = null;
            if (Application.Current is { } app) TrayIcon.SetIcons(app, new TrayIcons());
            remoteSessions.Changed -= BuildWorkspaceTree;
            MessageList.ItemsSource = null; AttachmentList.ItemsSource = null;
            runtimes.Clear(); terminals.Clear(); loginSessions.Clear(); discoveries.Clear(); workspaceClosures.Clear();
            foreach (var owned in desktopWindow!.OwnedWindows.ToArray()) owned.Close();
            if (errors.Count > 0) System.Diagnostics.Trace.WriteLine(new AggregateException("Shutdown cleanup errors", errors));
            shutdownComplete = true; desktopWindow?.Close();
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && ReferenceEquals(desktop.MainWindow, desktopWindow)) desktop.Shutdown();
        }
    }
}




