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
    public void OpenFileLink(string target)
    {
        if (workspace is null) throw new IOException("Select a workspace first.");
        FileLinks.Reveal(FileLinks.Resolve(target, workspace));
    }
    private readonly Store store;
    private bool restoringSelection = true;
    private SessionService remoteSessions = null!;
    private RemoteServer? remoteServer;
    private RemoteView? remoteView;
    private readonly Dictionary<RemoteHost, RemoteView> remoteViews = [];
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
    {
        if (remoteView is not null) { remoteView.SetPresentationSleeping(true); remoteView.IsVisible = false; }
        remoteView = null; MobileTerminalButton.IsEnabled = false;
        if (closing)
        {
            foreach (var view in remoteViews.Values) { view.Dispose(); RootPanes.Children.Remove(view); }
            remoteViews.Clear(); remoteCatalogs.Clear(); remoteWorkspaceVisuals.Clear(); remoteActivity.Clear();
        }
        else RefreshRemoteSidebar();
    }
    private void OpenRemoteHost(RemoteHost host, string? workspaceId = null)
    {
        ClearRecoveryNotice();
        if (remoteView?.Host == host) { remoteView.SetPresentationSleeping(false); if (workspaceId is not null) remoteView.SelectWorkspaceId(workspaceId); CollapseSidebar(); return; }
        CollapseSidebar(); CloseRemoteView();
        refreshingChats = true;
        try { foreach (var list in workspaceLists.Values) list.SelectedItem = null; }
        finally { refreshingChats = false; }
        if (current is not null) { current.Draft = Composer.Text ?? ""; pendingSaves.Add(current); DeferHistoryEviction(current); }
        MessageList.ItemsSource = null; AttachmentList.ItemsSource = null;
        if (remoteViews.TryGetValue(host, out var existing))
        {
            remoteView = existing; existing.IsVisible = true; existing.SetPresentationSleeping(false); MobileTerminalButton.IsEnabled = true;
            if (workspaceId is not null) existing.SelectWorkspaceId(workspaceId);
            RefreshRemoteSidebar(); return;
        }
        var view = new RemoteView(host, () => store.Setting("allowAllPermissions") == "1", () => { ShowFromTray(); OpenRemoteHost(host); }, store, remoteDownloads) { ShowTerminalButton = !remoteOnly }; remoteView = view; remoteViews[host] = view; MobileTerminalButton.IsEnabled = true;
        view.SetConnectionCollapsed(store.Setting(RemoteCollapsedKey(host)) == "1");
        if (workspaceId is not null) view.SelectWorkspaceId(workspaceId);
        view.WorkspaceNavigation += CollapseSidebar;
        view.CatalogChanged += catalog => { if (closing) return; remoteCatalogs[host] = catalog; RefreshRemoteSidebar(); };
        view.WorkspaceOpened += id => { store.Setting("closed:remote:" + host.Address + ":" + id, "0"); RefreshRemoteSidebar(); };
        Grid.SetColumn(view, 2); Grid.SetRow(view, 1); view.Bind(BackgroundProperty, this.GetResourceObservable("AppBackground")); RootPanes.Children.Add(view);
    }

    private readonly ObservableCollection<Workspace> workspaces;
    private readonly List<Chat> chats;
    private readonly Dictionary<string, ChatRuntime> runtimes = [];
    private readonly Dictionary<string, List<(TabItem Tab, TerminalSession Session)>> terminals = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<Chat> pendingSaves = [];
    private bool savingPending;
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
    private TranscriptNavigation? historyNavigation;
    private void UpdateHistoryNavigation() => historyNavigation?.Update();
    private HashSet<string> searchMatches = [];
    private CancellationTokenSource? searchCancellation;
    private Chat? configChat;
    private int configVersion = -1;
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
        InitializeDownloads();
        InitializeChatSearch();
        OpenWorkspaceButton.Content = AppIcons.Label("add", "Open workspace");
        ArchiveViewButton.Content = AppIcons.Label("chevron-down", "Chats", trailing: true);
        ((StackPanel)SlashCommands.Parent!).Children.Remove(SlashCommands);
        RootPanes.Children.Add(new SlashCommandOverlay(Composer, SlashCommands));
        historyNavigation = new TranscriptNavigation(MessageList, HistoryNavigation, () => viewingHistory, BrowseHistory);
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
        remoteSessions = new SessionService(store, workspaces, chats, Runtime) { DeleteChat = DeleteRemoteChat, CloseWorkspace = CloseWorkspace, RenameWorkspace = RenameWorkspace };
        remoteSessions.Changed += BuildWorkspaceTree;
        BuildWorkspaceTree();
        DragDrop.SetAllowDrop(ComposerBorder, true);
        ComposerBorder.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }, RoutingStrategies.Bubble, true);
        ComposerBorder.AddHandler(DragDrop.DropEvent, DropFiles, RoutingStrategies.Bubble, true);
        Composer.AddHandler(KeyDownEvent, ComposerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (palette is null && e.Key == Key.Oem3 && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; ToggleTerminal(this, new()); }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Handled || subagentInspector is not null || palette is not null || TerminalDrawer.IsVisible || remoteView is not null || e.Key != Key.Escape || current?.Busy != true || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            if (SlashCommands.IsVisible) { SlashCommands.IsVisible = false; return; }
            await InterruptDraft();
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; PaletteClick(this, new()); }
        }, RoutingStrategies.Tunnel);
        saveTimer.Tick += async (_, _) =>
        {
            if (savingPending || pendingSaves.Count == 0) return;
            savingPending = true;
            try { await SavePendingAsync(); await store.FlushAsync(); }
            catch (Exception error) { StatusText.Text = "Could not save: " + error.Message; }
            finally { savingPending = false; }
        }; saveTimer.Start();
        ChatPane.SizeChanged += (_, _) => PermissionScroll.MaxHeight = Math.Clamp(ChatPane.Bounds.Height * .35, 64, 280);
        InitializeLayout();
        if (workspaces.Count > 0) SelectWorkspace(workspaces.FirstOrDefault(w => w.Id == store.Setting("workspace")) ?? workspaces[0]);
        UpdateControls();
        AppDiagnostics.RecoveryRequested += RecoverAfterError;
        restoringSelection = false;
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
    public void OpenLocalDirectory(string directory)
    {
        if (remoteOnly) return;
        try
        {
            var path = WorkspaceLaunch.Normalize(directory);
            var existing = store.Workspaces().FirstOrDefault(w => !w.IsWsl && WorkspaceLaunch.SameLocalPath(w.Path, path));
            OpenWorkspace(existing ?? new Workspace(Guid.NewGuid().ToString("N"), Workspace.DefaultName(path), path));
        }
        catch (Exception error) when (!AppDiagnostics.IsUnrecoverable(error)) { StatusText.Text = AppDiagnostics.Message("Could not open workspace", error); }
    }
    private void OpenWorkspace(Workspace selected)
    {
        showArchived = false; ArchiveViewButton.Content = AppIcons.Label("chevron-down", "Chats", trailing: true); SearchBox.Text = "";
        var existing = store.Workspaces().FirstOrDefault(w => w.Path == selected.Path && w.Distro == selected.Distro) ?? selected;
        store.Save(existing); store.Setting("closed:" + existing.Id, "0");
        if (!workspaces.Any(w => w.Id == existing.Id)) workspaces.Add(existing);
        BuildWorkspaceTree(); SelectWorkspace(existing, true);
    }
    private async void WorkspaceSelectorClick(object? sender, RoutedEventArgs e)
    {
        if (RemoteSettings.Hosts(store).Count > 0)
        {
            var chooser = new WorkspaceComputerPicker(store, remoteOnly, remoteView?.Host) { Width = Math.Min(440, Math.Max(220, Bounds.Width - 32)), MaxHeight = Math.Max(300, Bounds.Height - 120) };
            var popup = new Flyout { Content = chooser };
            popup.Closed += (_, _) => chooser.Dispose();
            chooser.LocalChosen += selected => { popup.Hide(); OpenWorkspace(selected); };
            chooser.BrowseLocal += () => { popup.Hide(); OpenWorkspaceClick(this, new()); };
            chooser.RemoteChosen += (host, id) => { popup.Hide(); OpenRemoteHost(host, id); };
            chooser.PairComputer += async () =>
            {
                popup.Hide();
                if (remoteOnly) { ShowConnectionSettings(); return; }
                var host = await new RemoteSettings(store, remoteServer?.Fingerprint, ConfigureRemoteServer, ConfigureWebServer).ShowDialog<RemoteHost?>(desktopWindow!);
                BuildWorkspaceTree(); if (host is not null) OpenRemoteHost(host);
            };
            Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(OpenWorkspaceButton, popup); popup.ShowAt(OpenWorkspaceButton); return;
        }
        if (remoteOnly) { ShowConnectionSettings(); return; }
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
        Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(OpenWorkspaceButton, flyout);
        flyout.ShowAt(OpenWorkspaceButton);
        foreach (var item in history.Entries())
        {
            if (await WorkspaceHistory.Exists(item) == false && !closing) history.Remove(item);
            if (closing) return;
        }
        selector.Refresh(history.Entries());
    }
    private void SelectWorkspace(Workspace selected, bool startChat = false)
    {
        ClearRecoveryNotice();
        CloseRemoteView();
        if (workspace?.Id != selected.Id) ClearChat();
        workspace = selected; store.Setting("workspace", selected.Id);
        if (startChat) new WorkspaceHistory(store).Opened(selected);
        WorkspaceHeading.Text = $"{selected.Host}  /  {selected.Path}";
        RefreshChats();
        ChatList.SelectedItem = chats.FirstOrDefault(c => c.WorkspaceId == selected.Id && c.Archived == showArchived && c.Id == store.Setting("chat:" + selected.Id)) ?? ChatList.Items.FirstOrDefault();
        if (ChatList.SelectedItem is null) ClearChat();
        SwitchTerminalWorkspace(selected.Id);
        UpdateControls();
        if (startChat && !chats.Any(c => c.WorkspaceId == selected.Id && !c.Archived))
            NewChat(Enum.TryParse<AgentProvider>(store.Setting("lastProvider"), out var provider) ? provider : AgentProvider.Codex);
        // Sidebar restoration uses cached metadata. Import is explicit and may start an adapter.
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
        if (remoteView is not null) { RefreshRemoteSidebar(); return; }
        searchCancellation?.Cancel(); var cancellation = searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        searchMatches.Clear(); RefreshChats(); var query = SearchBox.Text ?? ""; if (query.Length == 0) return;
        try { await Task.Delay(150, cancellation.Token); var matches = await store.SearchChatIdsAsync(query, cancellation.Token); if (!cancellation.IsCancellationRequested) { searchMatches = matches; RefreshChats(); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Search failed: " + error.Message; }
    }
    private void BuildWorkspaceTree()
    {
        if (sidebarHolding || sidebarDragging) { sidebarRebuildPending = true; return; }
        var savedHosts = RemoteSettings.Hosts(store);
        foreach (var host in remoteViews.Keys.Where(host => !savedHosts.Contains(host)).ToArray())
        {
            var view = remoteViews[host]; if (ReferenceEquals(remoteView, view)) { remoteView = null; MobileTerminalButton.IsEnabled = false; }
            view.Dispose(); RootPanes.Children.Remove(view); remoteViews.Remove(host); remoteCatalogs.Remove(host);
            var scope = "remote:" + host.Address + ":" + host.Port + ":";
            foreach (var key in remoteActivity.Keys.Where(k => k.StartsWith(scope, StringComparison.Ordinal)).ToArray()) remoteActivity.Remove(key);
        }
        WorkspaceTree.Children.Clear(); workspaceLists.Clear(); remoteSections.Clear(); remoteWorkspaceVisuals.Clear();
        if (!remoteOnly && RemoteSettings.Hosts(store).Count > 0) WorkspaceTree.Children.Add(new TextBlock { Name = "LocalWorkspaceGroup", Text = "This computer", Margin = new Thickness(4, 6), Classes = { "muted" } });
        foreach (var owner in SidebarOrder.Apply(store, "workspaces", workspaces.Where(w => store.Setting("closed:" + w.Id) != "1"), w => w.Id))
        {
            var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto") };
            var title = new Button { Name = "Workspace_" + owner.Id, Content = owner.Name + (owner.IsWsl ? " · " + owner.Distro + " (WSL)" : ""), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(title, owner.Caption + " · " + owner.Path);
            title.Click += (_, _) => { if (SidebarTouchSwipe) return; if (showArchived) { SetWorkspaceExpanded(owner.Id, !WorkspaceExpanded(owner.Id)); BuildWorkspaceTree(); } else SelectWorkspace(owner, true); };
            var create = new IconButton { Name = "NewChat_" + owner.Id, Icon = "add", IconSize = 10, Label = "New chat", ClickAllowed = () => !SidebarTouchSwipe, Classes = { "rowAction" } };
            ToolTip.SetTip(create, "New chat"); Grid.SetColumn(create, 2);
            create.IsVisible = !showArchived; create.Flyout = ProviderMenu(owner);
            var rename = new IconButton { Name = "RenameWorkspace_" + owner.Id, Icon = "edit", IconSize = 10, Label = "Rename workspace", ClickAllowed = () => !SidebarTouchSwipe, Classes = { "rowAction" } };
            rename.Click += (_, _) => WorkspaceRename.Show(rename, owner.Name, async name => { await RenameWorkspace(owner, name); return true; });
            Grid.SetColumn(rename, 3);
            var close = new IconButton { Name = "CloseWorkspace_" + owner.Id, Icon = "remove", IconSize = 10, Label = "Close workspace (keep chats)", ClickAllowed = () => !SidebarTouchSwipe, Classes = { "rowAction" } };
            ToolTip.SetTip(close, "Close workspace (keep chats)"); Grid.SetColumn(close, 4);
            close.Click += async (_, _) =>
            {
                close.IsEnabled = false;
                var operation = CloseWorkspace(owner); workspaceClosures.Add(operation);
                try { await operation; }
                finally { workspaceClosures.Remove(operation); }
            };
            header.Children.Add(title); header.Children.Add(create); header.Children.Add(rename); header.Children.Add(close);
            var list = new SidebarChatList { Name = "Chats_" + owner.Id, Background = Brushes.Transparent, Tag = owner, Margin = new(8, 0, 0, 0), TouchSwipe = IsSidebarTouchSwipe };
            list.IsVisible = WorkspaceExpanded(owner.Id);
            var collapse = new IconButton { Name = "CollapseWorkspace_" + owner.Id, Icon = list.IsVisible ? "chevron-down" : "chevron-right", Label = list.IsVisible ? "Collapse workspace" : "Expand workspace", ClickAllowed = () => !SidebarTouchSwipe };
            collapse.Click += (_, _) => { list.IsVisible = !list.IsVisible; collapse.Icon = list.IsVisible ? "chevron-down" : "chevron-right"; collapse.Label = list.IsVisible ? "Collapse workspace" : "Expand workspace"; SetWorkspaceExpanded(owner.Id, list.IsVisible); };
            Grid.SetColumn(title, 1); header.Children.Add(collapse);
            list.ItemTemplate = new FuncDataTemplate<Chat>((chat, _) => chat is null ? null : SidebarChatRow(chat, async _ => await RenameChat(chat), () => ArchiveChat(chat)), false);
            list.SelectionChanged += ChatChanged; workspaceLists[owner.Id] = list;
            var group = new StackPanel { Background = SidebarColors.Brush(store, "workspaceColor:" + owner.Id, true) };
            var heading = new Grid { ColumnDefinitions = new("*,Auto"), Background = Brushes.Transparent, Classes = { "workspaceHeading" } };
            EnableHoldReorder(group, "workspaces", owner.Id, BuildWorkspaceTree, heading); heading.Children.Add(header);
            group.Children.Add(heading); group.Children.Add(list); group.Children.Add(WorkspaceDivider());
            ColorMenu(title, "workspaceColor:" + owner.Id, "Workspace background color", () => { BuildWorkspaceTree(); ApplyChatColors(); });
            WorkspaceTree.Children.Add(group);
        }
        foreach (var host in RemoteSettings.Hosts(store))
        {
            var collapsed = store.Setting(RemoteCollapsedKey(host)) == "1";
            var header = new Grid { ColumnDefinitions = new("Auto,*") };
            var collapse = new IconButton { Name = "CollapseConnection_" + host.Address + "_" + host.Port, Icon = collapsed ? "chevron-right" : "chevron-down", IconSize = 10, Label = collapsed ? "Expand connection" : "Collapse connection", ClickAllowed = () => !SidebarTouchSwipe };
            var button = new Button { Content = "Remote · " + host.Name + " (" + host.Address + ")", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            var section = new StackPanel { IsVisible = !collapsed }; remoteSections[host.Address + ":" + host.Port] = section;
            void SetCollapsed(bool value)
            {
                collapsed = value; store.Setting(RemoteCollapsedKey(host), value ? "1" : "0"); section.IsVisible = !value;
                collapse.Icon = value ? "chevron-right" : "chevron-down"; collapse.Label = value ? "Expand connection" : "Collapse connection";
                if (remoteViews.TryGetValue(host, out var view)) view.SetConnectionCollapsed(value);
                if (!value) { if (!showArchived) OpenRemoteHost(host); RefreshRemoteSidebar(); }
            }
            collapse.Click += (_, _) => SetCollapsed(!collapsed);
            button.Click += (_, _) => { if (SidebarTouchSwipe) return; if (showArchived) SetCollapsed(!collapsed); else if (collapsed) SetCollapsed(false); else OpenRemoteHost(host); };
            Grid.SetColumn(button, 1); header.Children.Add(collapse); header.Children.Add(button); WorkspaceTree.Children.Add(header); WorkspaceTree.Children.Add(section);
            if (remoteViews.TryGetValue(host, out var saved)) saved.SetConnectionCollapsed(collapsed);
        }
        RefreshChats();
        RefreshRemoteSidebar();
    }
    private MenuFlyout ProviderMenu(Workspace owner)
    {
        var menu = new MenuFlyout();
        foreach (var provider in AgentProviders.Enabled(store).Select(p => p.Provider))
        {
            var item = new MenuItem { Header = AgentProviders.Get(provider).Name, Icon = new Image { Source = BrandAssets.Provider(provider), Width = 16, Height = 16 } };
            item.Click += (_, _) => { SelectWorkspace(owner); NewChat(provider); };
            menu.Items.Add(item);
        }
        return menu;
    }
    private Task RenameWorkspace(Workspace owner, string name)
    {
        var index = workspaces.ToList().FindIndex(w => w.Id == owner.Id);
        if (index < 0) throw new IOException("Workspace is no longer open.");
        var updated = workspaces[index] with { Name = name };
        workspaces[index] = updated;
        if (workspace?.Id == owner.Id) workspace = updated;
        store.Save(updated); BuildWorkspaceTree(); UpdateControls();
        return Task.CompletedTask;
    }
    private async Task CloseWorkspace(Workspace owner)
    {
        await SaveAllAsync(); store.Setting("closed:" + owner.Id, "1");
        if (workspace?.Id == owner.Id) { ClearChat(); workspace = null; }
        workspaces.Remove(owner);
        if (terminals.Remove(owner.Id, out var shells)) foreach (var shell in shells) shell.Session.Dispose();
        foreach (var key in loginSessions.Keys.Where(k => k.StartsWith(owner.Id + ":", StringComparison.Ordinal)).ToArray())
        { loginSessions[key].Session.Dispose(); loginSessions.Remove(key); }
        foreach (var chat in chats.Where(c => c.WorkspaceId == owner.Id).ToArray())
            if (runtimes.Remove(chat.Id, out var runtime)) await runtime.DisposeAsync();
        await SaveAllAsync();
        if (closing) return;
        BuildWorkspaceTree();
        if (workspace is not null) SelectWorkspace(workspace);
        else if (workspaces.FirstOrDefault() is { } next) SelectWorkspace(next);
        else { store.Setting("workspace", ""); WorkspaceHeading.Text = "No workspace selected"; TerminalTabs.ItemsSource = null; TerminalDrawer.IsVisible = false; UpdateControls(); }
    }
    private void RefreshChats()
    {
        UpdateArchiveSelection();
        if (sidebarDragging || sidebarHolding) return;
        var query = SearchBox.Text ?? "";
        refreshingChats = true;
        foreach (var (id, list) in workspaceLists)
        {
            var selected = list.SelectedItem ?? (remoteView is null && current?.WorkspaceId == id ? current : null);
            IEnumerable<Chat> ordered = showArchived ? chats.Where(c => c.WorkspaceId == id).OrderByDescending(c => c.Updated).ThenBy(c => c.Id) : SidebarOrder.Apply(store, "chats:" + id, chats.Where(c => c.WorkspaceId == id), c => c.Id);
            var rows = ordered.Where(c => c.Archived == showArchived && (c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || searchMatches.Contains(c.Id))).ToArray();
            if (list.ItemsSource is not IEnumerable<Chat> previous || !previous.SequenceEqual(rows)) list.ItemsSource = rows;
            if (selected is not null && list.Items.Contains(selected)) list.SelectedItem = selected;
        }
        refreshingChats = false;
    }
    private void NewChat(AgentProvider provider = AgentProvider.Codex)
    {
        if (workspace is null) return;
        if (!AgentProviders.IsEnabled(store, provider))
        {
            var available = AgentProviders.Enabled(store).FirstOrDefault();
            if (available is null) { StatusText.Text = "Enable a provider in Settings > Agents to start a chat."; return; }
            provider = available.Provider;
        }
        if (showArchived) ShowArchiveView(false);
        var chat = new Chat { WorkspaceId = workspace.Id, Provider = provider }; store.Save(chat); chats.Insert(0, chat);
        SearchBox.Text = ""; RefreshChats(); ChatList.SelectedItem = chat;
        _ = Runtime(chat, workspace).Reconnect();
    }
    private async void ChatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingChats || sender is not ListBox { SelectedItem: Chat chat, Tag: Workspace owner } || chat.IsDeleting || chat.Archived || showArchived) return;
        if (!recoveringPresentation) ClearRecoveryNotice();
        var connectAgent = !restoringSelection;
        if (connectAgent) CollapseSidebar();
        if (!ReferenceEquals(current, chat)) { recoveryAttempts = 0; ChatSearch.Close(); }
        CloseRemoteView();
        if (workspace?.Id != owner.Id)
        {
            if (current is not null) { current.Draft = Composer.Text ?? ""; store.Save(current); }
            workspace = owner; store.Setting("workspace", owner.Id); WorkspaceHeading.Text = $"{owner.Host}  /  {owner.Path}";
            SwitchTerminalWorkspace(owner.Id);
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
        ScrollTranscriptToEnd(force: true);
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
        if (!connectAgent || !ReferenceEquals(current, chat) || remoteView is not null || closing) return;
        if (chat.SessionId is not null && (chat.Messages.Count == 0 || store.Setting("historyIncomplete:" + chat.Id) == "1") && workspace is not null)
            await Runtime(chat, workspace).LoadHistory();
        else if (chat.SessionId is not null && workspace is not null && Runtime(chat, workspace) is { IsConnected: false, IsReconnecting: false } runtime)
            await runtime.Reconnect();
    }
    private void DraftChanged(object? sender, TextChangedEventArgs e) { if (!switching && current is not null) { current.Draft = Composer.Text ?? ""; pendingSaves.Add(current); } UpdateSlashCommands(); UpdateComposerAction(); }
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
        var canSteer = current is not null && runtimes.GetValueOrDefault(current.Id) is { SupportsSteering: true, IsPrompting: true, IsSteering: false };
        foreach (var button in QueueItems.GetVisualDescendants().OfType<IconButton>().Where(b => b.Name == "SteerQueued")) button.IsVisible = canSteer;
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
                var button = new IconButton { Icon = icon, Label = label, Name = icon == "steer" ? "SteerQueued" : null, IsVisible = icon != "steer" || canSteer };
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
    private async Task InterruptDraft()
    {
        if (current is not { } chat || workspace is not { } owner) return;
        var runtime = Runtime(chat, owner);
        var text = Composer.Text ?? ""; var attachments = chat.Attachments.ToArray();
        if (string.IsNullOrWhiteSpace(text) && attachments.Length == 0)
        {
            if (chat.QueuedInputs.Count > 0) await runtime.AdvanceQueued(interrupt: true); else await runtime.Stop();
            return;
        }
        var input = new PendingInput(text, attachments);
        runtime.Queue(input);
        Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear(); store.Save(chat);
        await runtime.AdvanceQueued(interrupt: true);
        UpdateControls();
    }
    private async Task SteerDraft()
    {
        if (current is not { } chat || workspace is not { } owner) return;
        var runtime = Runtime(chat, owner);
        if (!runtime.SupportsSteering) { await Send(); return; }
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
        ApplyChatColors();
        UpdatePermissions();
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
                    if (option.Id == VtCodeLaunch.AuthenticationOption)
                    {
                        var badge = new OptionContent(option) { Margin = new Thickness(4, 2) }; ToolTip.SetTip(badge, option.Name);
                        ConfigOptionsPanel.Children.Add(badge); continue;
                    }
                    var picker = new Button { Name = "Config_" + option.Id, Content = new OptionContent(option, configured.Provider), MaxWidth = 190, MinHeight = 24, FontSize = 11, Padding = new Thickness(4, 2) };
                    ToolTip.SetTip(picker, option.Name);
                    if (configured.Provider == AgentProvider.OpenCode && ModelPicker.IsModel(option))
                    {
                        picker.Flyout = ModelPicker.Create(option, ModelPicker.Recent(store, configured.Provider), value => Runtime(configured, owner).SetConfig(option, value), () => ModelPicker.Recent(store, configured.Provider), () => !compact && !OperatingSystem.IsAndroid());
                        ConfigOptionsPanel.Children.Add(picker); continue;
                    }
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
        Welcome.IsVisible = current is null;
        WelcomeHeading.Text = remoteOnly ? "Connect to your computer" : workspace is null ? "Open a folder to start" : "Start a chat in " + workspace.Name;
        WelcomeHint.Text = remoteOnly ? "Enter its address, then the pairing number shown on your desktop." : workspace is null ? "Your chats and terminals stay with the project." : "Choose Claude, Codex, or OpenCode from ＋ beside the workspace.";
        ComposerBorder.IsVisible = !remoteOnly;
        WelcomeOpenButton.IsVisible = workspace is null;
        CopyChatButton.IsEnabled = current is not null;
        MessageList.IsVisible = current is not null; UpdateHistoryNavigation();
        ComposerBorder.IsEnabled = current is not null;
        ImportChatsButton.IsEnabled = workspace is not null || remoteView?.HasWorkspace == true;
        LoginButton.IsEnabled = workspace is not null;
        var provider = current?.Provider ?? AgentProvider.Codex;
        LoginButton.Content = provider == AgentProvider.OpenCode ? "Add provider" : "Log in to " + AgentProviders.Get(provider).Name;
        var login = loginSessions.GetValueOrDefault($"{workspace?.Id}:{provider}");
        LoginPanel.IsVisible = workspace is not null && (current?.NeedsLogin == true || authentication.GetValueOrDefault($"{workspace.Distro}:{provider}") || login.Control is not null);
        LoginHeading.Text = $"Sign in to {AgentProviders.Get(provider).Name} in {workspace?.Host} to continue this chat.";
        LoginTerminalHost.Content = login.Control;
        LoginTerminalHost.IsVisible = login.Control is not null;
        PasteLoginButton.IsVisible = login.Control is not null;
        LoginButton.IsVisible = login.Control is null;
        LoginUrl.Text = login.Session?.Output.Link ?? "";
        LoginLinkPanel.IsVisible = login.Session?.Output.Link is not null;
        ChatHeading.Text = current?.Title ?? "Select a chat";
        ChatProviderIcon.Source = current is null ? null : BrandAssets.Provider(current.Provider);
        ToolTip.SetTip(ChatProviderIcon, current?.ProviderLabel);
        Composer.PlaceholderText = current is null ? "Message…" : $"Message {AgentProviders.Get(current.Provider).Name}…";
        UpdateComposerAction();
        ReconnectChatButton.IsEnabled = current is not null && runtimes.GetValueOrDefault(current.Id) is not { IsReconnecting: true } and not { IsLoadingHistory: true };
        ResumeChatButton.IsVisible = current?.InterruptedInput is not null && current.Busy == false && runtimes.GetValueOrDefault(current.Id) is not { IsRecovering: true };
        ArchiveChatButton.IsEnabled = current is not null;
        ArchiveChatButton.Label = current?.Archived == true ? "Restore chat" : "Archive chat";
        DeleteChatButton.IsEnabled = current is { Busy: false };
        StatusText.Text = current is null ? "Ready — open a workspace to begin" : $"{workspace?.Host}  ·  {current.Status}";
    }
    private bool ComposerLoading => current is { } chat && (runtimes.GetValueOrDefault(chat.Id)?.IsRecovering == true || chat.Busy && (runtimes.GetValueOrDefault(chat.Id)?.IsPreparing ?? true));
    private bool ComposerShowsStop => current?.Busy == true && !ComposerLoading && string.IsNullOrWhiteSpace(Composer.Text) && current.Attachments.Count == 0;
    private void UpdateComposerAction()
    {
        TranscriptPanel.SetShowProgress(MessageList, !viewingHistory && current is { Busy: true, NeedsPermission: false, NeedsLogin: false } && !ComposerLoading);
        var stop = ComposerShowsStop;
        SendButton.Icon = ComposerLoading ? "connecting" : stop ? "stop" : "send";
        SendButton.Label = ComposerLoading ? "Queue message (Enter)" : stop ? "Stop" : current?.Busy == true ? "Queue message (Enter)" : "Send (Enter)";
        var runtime = current is null ? null : runtimes.GetValueOrDefault(current.Id);
        SendButton.IsEnabled = current is not null && (runtime?.IsReconnecting == true ? !string.IsNullOrWhiteSpace(Composer.Text) || current.Attachments.Count > 0 : !ComposerLoading && (stop || (runtime is not { IsConfiguring: true } && (!current.Busy || runtime?.IsPrompting == true))));
    }
    private async void SendClick(object? sender, RoutedEventArgs e)
    {
        if (ComposerShowsStop) StopClick(sender, e); else await Send();
    }
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
        if (!runtime.IsReconnecting && (runtime.IsConfiguring || runtime.IsRecovering)) return;
        if (chat.Busy || runtime.IsReconnecting)
        {
            if (!runtime.IsPrompting && !runtime.IsReconnecting) return;
            runtime.Queue(new(text, attachments)); Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear(); store.Save(chat); UpdateControls();
            viewingHistory = false; pageLoad?.Cancel(); MessageList.ItemsSource = chat.Messages;
            ScrollTranscriptToEnd(force: true);
            return;
        }
        viewingHistory = false; pageLoad?.Cancel(); MessageList.ItemsSource = chat.Messages;
        Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear();
        ScrollTranscriptToEnd(force: true);
        await runtime.Send(text, attachments);
        if (ReferenceEquals(chat, current)) Composer.Text = chat.Draft;
        UpdateControls();
    }
    private ChatRuntime Runtime(Chat chat, Workspace owner)
    {
        if (!runtimes.TryGetValue(chat.Id, out var runtime))
        {
            runtime = new(chat, owner, store, AgentProviders.Command(store, owner, chat.Provider));
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                runtime.BackendMaintenance = BackendMaintenance;
            runtime.ResolveCommand = () => AgentProviders.Command(store, owner, chat.Provider);
            runtime.AuthenticationSucceeded += () => authentication[$"{owner.Distro}:{chat.Provider}"] = false;
            runtime.IsActiveView = () => ReferenceEquals(current, chat) && remoteView is null && desktopWindow is { IsVisible: true, IsActive: true } && !closing;
            runtime.Permission = (request, token) => Permission(chat, request, token);
            runtime.Changed += () =>
            {
                if (closing) return;
                pendingSaves.Add(chat);
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
                        var follow = MessageList.ItemCount == 0 || MessageList.ItemsPanelRoot is TranscriptPanel { IsFollowingEnd: true };
                        MessageList.ItemsSource = chat.Messages;
                        if (follow)
                        {
                            if (MessageList.ItemsPanelRoot is TranscriptPanel panel) panel.FollowEnd();
                            ScrollTranscriptToEnd();
                        }
                    }
                    else if (MessageList.ItemsPanelRoot is TranscriptPanel { IsFollowingEnd: true })
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
        if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        { e.Handled = true; Composer.SelectedText = "\n"; return; }
        if (SlashCommands.IsVisible && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.Enter or Key.Tab) { e.Handled = true; InsertSlashCommand(); return; }
            if (e.Key is Key.Up or Key.Down) { e.Handled = true; SlashCommands.SelectedIndex = Math.Clamp(SlashCommands.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, SlashCommands.ItemCount - 1); SlashCommands.ScrollIntoView(SlashCommands.SelectedItem!); return; }
            if (e.Key == Key.Escape) { e.Handled = true; SlashCommands.IsVisible = false; return; }
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (current is { } chat && workspace is { } owner && string.IsNullOrWhiteSpace(Composer.Text) && chat.Attachments.Count == 0)
                await Runtime(chat, owner).AdvanceQueued();
            else await Send();
        }
        else if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            await PasteClipboard(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (e.Key == Key.Escape && current?.Busy == true)
        {
            e.Handled = true;
            await InterruptDraft();
        }
    }
    private async void AttachFiles(object? sender, RoutedEventArgs e)
    {
        if (current is not { } target) return;
        if (sender is Control button) button.IsEnabled = false;
        try
        {
            IReadOnlyList<IStorageFile> files;
            using (MobileAppSecurity.BeginFilePicker())
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Reference files or images", AllowMultiple = true });
            try { if (!closing) await AddFiles(files, target); }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) StatusText.Text = "Could not attach files: " + error.Message; }
        finally { if (sender is Control control) control.IsEnabled = true; }
    }
    private async void DropFiles(object? sender, DragEventArgs e)
    {
        e.Handled = true; if (current is not { } target) return;
        try
        {
            var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
            if (files.Length > 0) await AddFiles(files, target);
            else if (AttachmentClipboard.Image(e.DataTransfer) is { } image) { target.Attachments.Add(image); pendingSaves.Add(target); }
        }
        catch (Exception error) { StatusText.Text = "Drop failed: " + error.Message; }
    }
    private async Task AddFiles(IEnumerable<IStorageItem> files, Chat? target = null)
    {
        target ??= current; if (target is null) return;
        foreach (var file in files)
        {
            try
            {
                if (file is not IStorageFile storageFile) throw new IOException("Drop individual files, or use Open workspace for a folder.");
                var attachment = await AttachmentFiles.Read(storageFile, discoveryLifetime.Token);
                if (closing || !chats.Contains(target)) return;
                target.Attachments.Add(attachment);
                pendingSaves.Add(target);
            }
            catch (Exception ex) { StatusText.Text = file.Name + ": " + ex.Message; }
        }
    }
    private void RemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Attachment attachment } || current is null) return;
        current.Attachments.Remove(attachment);
        pendingSaves.Add(current);
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
                if (await AttachmentClipboard.Image(data) is { } image)
                {
                    var index = 1;
                    while (target.Attachments.Any(a => a.Reference == $"[Image #{index}]")) index++;
                    var reference = $"[Image #{index}]";
                    target.Attachments.Add(image with { Name = $"Pasted image {index}" + Path.GetExtension(image.Name), Reference = reference });
                    pendingSaves.Add(target);
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
    private readonly Dictionary<string, (Chat Chat, PermissionCard Card)> permissionCards = [];
    private void UpdatePermissions()
    {
        InlinePermissions.Children.Clear();
        foreach (var pending in permissionCards.Values.Where(p => p.Chat == current)) InlinePermissions.Children.Add(pending.Card);
        PermissionScroll.IsVisible = InlinePermissions.Children.Count > 0;
    }
    private async Task<JsonObject> Permission(Chat chat, JsonElement request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = remoteSessions.RegisterPermission(chat, request, completion);
        using var cancellation = token.Register(() => completion.TrySetResult(RpcJson.Permission()));
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (completion.Task.IsCompleted) return;
                permissionCards[id] = (chat, new PermissionCard(JsonNode.Parse(request.GetRawText())!.AsObject(), option =>
                { completion.TrySetResult(RpcJson.Permission(option)); return Task.CompletedTask; }));
                UpdateControls(); UpdatePermissions();
                PermissionNotifications.Show(id, chat.Title, () => { ShowFromTray(); SearchBox.Text = ""; if (workspaces.FirstOrDefault(w => w.Id == chat.WorkspaceId) is { } owner) { SelectWorkspace(owner); ChatList.SelectedItem = chat; } });
            });
            return await completion.Task;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { remoteSessions.ForgetPermission(id); permissionCards.Remove(id); PermissionNotifications.Dismiss(id); UpdatePermissions(); UpdateControls(); });
        }
    }
    private async void ImportChatsClick(object? sender, RoutedEventArgs e)
    {
        if (remoteView is not null) { remoteView.ShowImport(ImportChatsButton); return; }
        if (workspace is not { } owner) return;
        var dialog = new Window { Title = "Import chats", Width = 400, Height = 290, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var options = AgentProviders.Enabled(store).Select(p => new CheckBox { Content = p.Label, Tag = p.Provider, IsChecked = p.Provider != AgentProvider.Codex }).ToArray();
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
        try { await ShowSettings(); }
        catch (Exception error) { StatusText.Text = AppDiagnostics.Message("Could not open settings", error); }
    }
    private Window? settingsWindow;
    private ListBox? settingsNavigation;
    private Task ShowSettings(string category = "General")
    {
        if (remoteOnly) { ShowConnectionSettings(); return Task.CompletedTask; }
        if (settingsWindow is not null) { settingsNavigation!.SelectedItem = category; settingsWindow.Activate(); return Task.CompletedTask; }
        var dialog = new Window { Title = "Settings", Width = 820, Height = 640, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var general = new StackPanel { Spacing = 16 };
        var appearance = new StackPanel { Spacing = 12 };
        var agents = new StackPanel { Spacing = 8 };
        var panel = general;
        var saveError = new TextBlock { Name = "SettingsSaveError", TextWrapping = TextWrapping.Wrap };
        void ApplyChange(Action change) { try { change(); saveError.Text = ""; } catch (Exception error) { saveError.Text = AppDiagnostics.Message("Could not apply settings", error); } }
        var sleep = new CheckBox { Name = "PresentationSleep", Content = "Sleep UI when hidden or inactive (after 2 seconds)", IsChecked = store.Setting("presentationSleep") == "1" };
        sleep.IsCheckedChanged += (_, _) => ApplyChange(() => { store.Setting("presentationSleep", sleep.IsChecked == true ? "1" : "0"); SchedulePresentationSleep(); });
        panel.Children.Add(sleep);
        var allowAll = new CheckBox { Name = "AllowAllPermissions", Content = "Allow all permission prompts", IsChecked = store.Setting("allowAllPermissions") == "1" };
        allowAll.IsCheckedChanged += (_, _) => ApplyChange(() => store.Setting("allowAllPermissions", allowAll.IsChecked == true ? "1" : "0"));
        panel.Children.Add(allowAll);
        var syntax = new CheckBox { Name = "SyntaxHighlighting", Content = "Syntax highlighting in code blocks", IsChecked = store.Setting("syntaxHighlighting") != "0" };
        syntax.IsCheckedChanged += (_, _) => ApplyChange(() => { var enabled = syntax.IsChecked == true; store.Setting("syntaxHighlighting", enabled ? "1" : "0"); AppTheme.SetSyntaxHighlighting(enabled); });
        appearance.Children.Add(syntax);
        var traySetting = new CheckBox { Name = "RunInTray", Content = "Keep agents running in the system tray when the window closes", IsChecked = store.Setting("runInTray") != "0" };
        traySetting.IsCheckedChanged += (_, _) => ApplyChange(() => { store.Setting("runInTray", traySetting.IsChecked == true ? "1" : "0"); ConfigureTray(); });
        panel.Children.Add(traySetting);
        var autoResume = new CheckBox { Name = "AutoResumeInterrupted", Content = "Automatically resume interrupted chats when the app starts", IsChecked = store.Setting("autoResume") == "1" };
        autoResume.IsCheckedChanged += (_, _) => ApplyChange(() => store.Setting("autoResume", autoResume.IsChecked == true ? "1" : "0"));
        var startAtLogin = new CheckBox { Name = "StartAtLogin", Content = "Start Vibe Harder when I sign in (including after a restart)", IsChecked = store.Setting("startAtLogin") == "1" };
        startAtLogin.IsCheckedChanged += (_, _) => ApplyChange(() => { StartupRegistration.SetEnabled(startAtLogin.IsChecked == true); store.Setting("startAtLogin", startAtLogin.IsChecked == true ? "1" : "0"); });
        panel.Children.Add(autoResume); panel.Children.Add(startAtLogin);
        panel.Children.Add(new TextBlock { Text = "Enable both for unattended recovery after a restart. Explicitly stopped chats stay stopped. Agents still use their existing permission settings.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var diagnostics = new Button { Content = "Open diagnostic logs" };
        diagnostics.Click += (_, _) => ApplyChange(() => { Directory.CreateDirectory(AppDiagnostics.DirectoryPath); FileLinks.Reveal(AppDiagnostics.DirectoryPath); }); panel.Children.Add(diagnostics);
        appearance.Children.Add(new TextBlock { Text = "Color theme" }); appearance.Children.Add(AppTheme.Picker(store));
        appearance.Children.Add(new Separator()); appearance.Children.Add(FontSettings.CreateContent(store));
        var connections = new ConnectionSettingsView(store, false, ConfigureRemoteServer, ConfigureWebServer);
        connections.Paired += host => { BuildWorkspaceTree(); OpenRemoteHost(host); desktopWindow?.Activate(); };
        agents.Children.Add(new TextBlock { Text = "Choose agents for new chats. Existing chats are kept. Expand a provider to configure its commands.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var backendAutoUpdate = new CheckBox { Name = "AutoUpdateBackends", Content = "Keep enabled agent backends up to date", IsChecked = BackendUpdates.Enabled(store) };
        ToolTip.SetTip(backendAutoUpdate, "Check at startup, every six hours, on reconnect, and when you check for app updates. Backends in use are skipped.");
        backendAutoUpdate.IsCheckedChanged += (_, _) => ApplyChange(() => store.Setting(BackendUpdates.EnabledKey, backendAutoUpdate.IsChecked == true ? "1" : "0"));
        agents.Children.Add(backendAutoUpdate);
        var fields = new Dictionary<string, TextBox>();
        foreach (var provider in AgentProviders.All)
        {
            panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 8) };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new Image { Source = BrandAssets.Provider(provider.Provider), Width = 16, Height = 16 });
            label.Children.Add(new TextBlock { Text = provider.Label, VerticalAlignment = VerticalAlignment.Center });
            var enabled = new CheckBox { Name = $"{provider.Provider}Enabled", Content = label, IsChecked = AgentProviders.IsEnabled(store, provider.Provider) };
            enabled.IsCheckedChanged += async (_, _) =>
            {
                ApplyChange(() => { store.Setting(AgentProviders.EnabledKey(provider.Provider), enabled.IsChecked == true ? "1" : "0"); BuildWorkspaceTree(); });
                if (enabled.IsChecked != true) return;
                saveError.Text = "Checking " + provider.Name + " installation…";
                try
                {
                    await BackendMaintenance.Check(discoveryLifetime.Token, force: true, installProvider: provider.Provider);
                    if (!closing) saveError.Text = BackendMaintenance.LastSummary;
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!closing) saveError.Text = error.Message; }
            };
            var commands = new Expander { Name = $"{provider.Provider}Commands", Header = enabled, Content = panel, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            var section = new StackPanel { Spacing = 4 };
            section.Children.Add(commands); section.Children.Add(new Separator()); agents.Children.Add(section);
            foreach (var isWsl in new[] { false, true })
            {
                if (provider.Provider == AgentProvider.VTCode)
                {
                    var owner = new Workspace("settings", "", "/", isWsl ? "WSL" : null);
                    var authentication = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "ChatGPT subscription (no API-key fallback)", "API key — billed separately" }, SelectedIndex = VtCodeLaunch.Method(store, owner) == "api_key" ? 1 : 0 };
                    authentication.SelectionChanged += (_, _) => ApplyChange(() => store.Setting(VtCodeLaunch.AuthenticationKey(owner), authentication.SelectedIndex == 1 ? "api_key" : "chatgpt"));
                    panel.Children.Add(new TextBlock { Text = (isWsl ? "WSL" : "Local") + " OpenAI authentication (reconnect to apply)", Classes = { "muted" } }); panel.Children.Add(authentication);
                }
                var key = AgentProviders.CommandKey(provider.Provider, isWsl);
                var field = new TextBox { Text = AgentProviders.Command(store, new Workspace("settings", "", "/", isWsl ? "WSL" : null), provider.Provider), TextWrapping = TextWrapping.Wrap };
                fields[key] = field;
                foreach (var updateHost in isWsl ? workspaces.Where(w => w.IsWsl).Select(w => w.Distro!).Distinct() : ["local"])
                    if (store.Setting("backendUpdate:" + updateHost + ":" + provider.Provider) is { } updateStatus)
                        panel.Children.Add(new TextBlock { Text = updateHost + ": " + updateStatus, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
                var loginKey = $"{provider.Provider}:{(isWsl ? "wsl" : "local")}LoginCommand";
                var loginField = new TextBox { Text = AgentProviders.LoginCommand(store, new Workspace("settings", "", "/", isWsl ? "WSL" : null), provider.Provider), TextWrapping = TextWrapping.Wrap };
                fields[loginKey] = loginField;
                panel.Children.Add(new TextBlock { Text = isWsl ? "WSL command" : "Local command", Classes = { "muted" } }); panel.Children.Add(field);
                panel.Children.Add(new TextBlock { Text = "Account setup command", Classes = { "muted" } }); panel.Children.Add(loginField);
            }
            if (provider.Provider == AgentProvider.Codex)
                foreach (var key in new[] { "localCodexCommand", "wslCodexCommand" })
                {
                    panel.Children.Add(new TextBlock { Text = key.StartsWith("wsl") ? "WSL Codex deletion command" : "Local Codex deletion command", Classes = { "muted" } });
                    var field = new TextBox { Text = store.Setting(key) ?? ChatHistory.DefaultCodexCommand, TextWrapping = TextWrapping.Wrap }; fields[key] = field; panel.Children.Add(field);
                }
        }
        panel = agents;
        var save = new Button { Name = "SaveSettings", Content = "Apply agent commands", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(save);
        async void SaveAgentCommands(object? sender, RoutedEventArgs args)
        {
            if (fields.Values.Any(f => string.IsNullOrWhiteSpace(f.Text))) { saveError.Text = "Fill in all agent commands before saving."; return; }
            save.IsEnabled = false; saveError.Text = "";
            try
            {
                if ((startAtLogin.IsChecked == true) != (store.Setting("startAtLogin") == "1")) StartupRegistration.SetEnabled(startAtLogin.IsChecked == true);
                foreach (var (key, field) in fields) store.Setting(key, field.Text!);
                store.Setting("autoResume", autoResume.IsChecked == true ? "1" : "0"); store.Setting("startAtLogin", startAtLogin.IsChecked == true ? "1" : "0");
                store.Setting("runInTray", traySetting.IsChecked == true ? "1" : "0"); ConfigureTray();
                await store.FlushAsync(); saveError.Text = "Agent commands saved.";
            }
            catch (Exception error) { saveError.Text = AppDiagnostics.Message("Could not save settings", error); }
            finally { save.IsEnabled = true; }
        }
        save.Click += SaveAgentCommands;
        var pages = new Dictionary<string, Control> { ["General"] = general, ["Appearance"] = appearance, ["Agents"] = agents, ["Terminal"] = TerminalSettings.CreateContent(store, store.Workspaces()), ["Connections"] = connections };
        var navigation = new ListBox { Name = "SettingsCategories", Classes = { "settingsNavigation" }, ItemsSource = pages.Keys.ToArray(), Background = Brushes.Transparent, Margin = new Thickness(8, 16) };
        var content = new Grid { Margin = new Thickness(24, 20), RowDefinitions = new("Auto,*,Auto") };
        var heading = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 20) }; content.Children.Add(heading);
        var pageViews = new Dictionary<string, Control>();
        foreach (var (name, page) in pages)
        {
            Control scroll = page == connections ? page : new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            scroll.IsVisible = false; Grid.SetRow(scroll, 1); content.Children.Add(scroll); pageViews[name] = scroll;
        }
        Grid.SetRow(saveError, 2); content.Children.Add(saveError);
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is string name) { heading.Text = name; foreach (var item in pageViews) item.Value.IsVisible = item.Key == name; } };
        var root = new Grid { ColumnDefinitions = new("160,*") };
        var navigationBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = navigation };
        navigationBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
        navigationBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorder")); root.Children.Add(navigationBorder);
        Grid.SetColumn(content, 1); root.Children.Add(content);
        dialog.Content = root; settingsWindow = dialog; settingsNavigation = navigation; navigation.SelectedItem = category;
        dialog.Closed += (_, _) => { connections.Dispose(); settingsWindow = null; settingsNavigation = null; if (!closing) BuildWorkspaceTree(); };
        dialog.Show(desktopWindow!);
        return Task.CompletedTask;
    }
    private async void PaletteClick(object? sender, RoutedEventArgs e)
    {
        if (remoteOnly) { ShowMobilePalette(); return; }
        if (palette is not null) { palette.Activate(); return; }
        var owner = workspace;
        List<PaletteCommand> commands = [
            new("Open workspace", "Choose a local or WSL folder", () => { OpenWorkspaceClick(this, new()); return Task.CompletedTask; }),
            new("Connection settings", "Configure agent and account commands", () => { SettingsClick(this, new()); return Task.CompletedTask; })
        ];
        commands.Add(new("Terminal settings", "Choose a shell for each platform", () => ShowSettings("Terminal")));
        if (owner is not null)
        {
            commands.Add(new("New terminal", owner.Caption, () => NewTerminal(target: owner)));
            commands.Add(new("Import previous chats", owner.Caption, () => { ImportChatsClick(this, new()); return Task.CompletedTask; }));
            foreach (var provider in AgentProviders.Enabled(store))
            {
                commands.Add(new("New " + provider.Name + " chat", provider.Label, () => { SelectWorkspace(owner); NewChat(provider.Provider); return Task.CompletedTask; }));
                commands.Add(new(provider.Provider == AgentProvider.OpenCode ? "OpenCode: add provider" : provider.Name + ": log in", owner.Host + " terminal", () => Login(owner, provider.Provider)));
            }
            if (current is { } chat) commands.Add(new("Reconnect current chat", chat.ProviderLabel, () => Runtime(chat, owner).Reconnect()));
        }
        palette = new CommandPalette(commands, owner);
        try
        {
            var command = await palette.Open(RootPanes);
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
    private TerminalControl CreateTerminalControl(TerminalSession session, int fontSize, Workspace owner)
    {
        var control = new ThemedTerminalControl { FileWorkspace = owner, Model = session.Model, FontSize = fontSize, FontFamily = new FontFamily("avares://VibeHarder.UI/Assets/Fonts#NeoSpleen Nerd Font") };
        control.Bind(TerminalControl.FontFamilyProperty, this.GetResourceObservable("TerminalFont"));
        control.Bind(TerminalControl.FontSizeProperty, this.GetResourceObservable("TerminalFontSize"));
        return control;
    }
    private async void PasteLoginClick(object? sender, RoutedEventArgs e)
    {
        if (LoginTerminalHost.Content is not ThemedTerminalControl terminal) return;
        await terminal.PasteText();
        terminal.Focus();
    }
    private async Task Login(Workspace owner, AgentProvider provider)
    {
        var key = $"{owner.Id}:{provider}";
        if (loginSessions.ContainsKey(key)) { UpdateControls(); loginSessions[key].Control.Focus(); return; }
        var session = new TerminalSession();
        var control = CreateTerminalControl(session, 12, owner);
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
        if (TerminalTabs.ItemCount == 0)
        {
            var saved = workspace is null ? [] : (store.Setting("terminalTabs:" + workspace.Id) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var id in saved) await NewTerminal(durableId: id);
        }
        else if (TerminalTabs.SelectedItem is TabItem { Content: Control terminal }) terminal.Focus();
    }
    private async void NewTerminalClick(object? sender, RoutedEventArgs e) => await NewTerminal();
    private async Task<TabItem?> NewTerminal(string? command = null, string? title = null, Workspace? target = null, Action? completed = null, string? durableId = null)
    {
        if (remoteOnly) return null;
        var owner = target ?? workspace;
        if (owner is null) return null;
        durableId = command is null ? durableId ?? "desktop:" + Guid.NewGuid().ToString("N") : null;
        var session = new TerminalSession();
        if (completed is not null) session.Completed += completed;
        var control = CreateTerminalControl(session, 13, owner);
        var tab = new TabItem { Content = control, MinHeight = 26, Padding = new(6, 2) };
        var close = new IconButton { Icon = "remove", IconSize = 10, Label = "Close terminal", Padding = new Thickness(3, 0), MinHeight = 18, Height = 18 };
        var reconnect = new IconButton { Name = "ReconnectTerminal", Icon = "refresh", IconSize = 10, Label = "Reconnect terminal", Padding = new Thickness(3, 0), MinHeight = 18, Height = 18, IsVisible = durableId is not null, IsEnabled = false };
        var list = terminals.GetValueOrDefault(owner.Id);
        if (list is null) terminals[owner.Id] = list = [];
        tab.Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { FontSize = 11, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, Text = title ?? $"{owner.Host} {list.Count + 1}", VerticalAlignment = VerticalAlignment.Center }, reconnect, close } };
        list.Add((tab, session));
        reconnect.Click += async (_, _) =>
        {
            if (!reconnect.IsEnabled || closing) return;
            reconnect.IsEnabled = false; close.IsEnabled = false;
            var replacement = new TerminalSession();
            try
            {
                await replacement.Start(owner, settings: store, durableId: durableId);
                var index = list.FindIndex(t => t.Tab == tab);
                if (closing || index < 0) { replacement.Dispose(); return; }
                session.Dispose(); session = replacement;
                if (completed is not null) session.Completed += completed;
                control = CreateTerminalControl(session, 13, owner); tab.Content = control;
                list[index] = (tab, session); control.Focus();
            }
            catch (Exception error) { replacement.Dispose(); StatusText.Text = "Could not reconnect terminal: " + error.Message; }
            finally { reconnect.IsEnabled = true; close.IsEnabled = true; }
        };
        close.Click += async (_, _) =>
        {
            try { await session.Close(); }
            catch (Exception error) { StatusText.Text = "Could not close terminal: " + error.Message; return; }
            list.RemoveAll(t => t.Tab == tab);
            store.Setting("terminalTabs:" + owner.Id, string.Join("\n", list.Select(t => t.Session.DurableId).OfType<string>()));
            if (terminalPaneWorkspace == owner.Id) { TerminalTabs.ItemsSource = list.Select(t => t.Tab).ToArray(); if (list.Count > 0) TerminalTabs.SelectedIndex = 0; }
        };
        if (workspace?.Id == owner.Id) { SwitchTerminalWorkspace(owner.Id); TerminalTabs.ItemsSource = list.Select(t => t.Tab).ToArray(); TerminalTabs.SelectedItem = tab; TerminalDrawer.IsVisible = true; }
        try
        {
            if (durableId is not null)
            {
                store.Setting("terminalTabs:" + owner.Id, string.Join("\n", list.Select(t => t.Session.DurableId).OfType<string>().Append(durableId).Distinct()));
                await store.FlushAsync();
            }
            await session.Start(owner, command, store, durableId);
            store.Setting("terminalTabs:" + owner.Id, string.Join("\n", list.Select(t => t.Session.DurableId).OfType<string>()));
            control.Focus(); return tab;
        }
        catch (Exception error) { session.Dispose(); session.Model.Feed("Could not start terminal: " + error.Message); StatusText.Text = error.Message; return null; }
        finally { reconnect.IsEnabled = true; }
    }
    private void TerminalResizeStart(object? sender, PointerPressedEventArgs e) { resizeY = e.GetPosition(this).X; resizeHeight = TerminalDrawer.Width; e.Pointer.Capture(sender as IInputElement); }
    private void TerminalResizeMove(object? sender, PointerEventArgs e) { if (resizeY is { } y) TerminalDrawer.Width = Math.Clamp(resizeHeight + y - e.GetPosition(this).X, 220, Math.Max(220, Bounds.Width - 650)); }
    private void TerminalResizeEnd(object? sender, PointerReleasedEventArgs e) { resizeY = null; e.Pointer.Capture(null); }
    private async Task SaveAllAsync()
    {
        store.Setting("sidebarWidth", (!compact && sidebarOpen ? RootPanes.ColumnDefinitions[0].ActualWidth : sidebarWidth).ToString(System.Globalization.CultureInfo.InvariantCulture));
        store.Setting("terminalWidth", TerminalDrawer.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var chat in chats.ToArray())
        {
            if (runtimes.GetValueOrDefault(chat.Id)?.IsLoadingHistory == true) continue;
            store.Save(chat);
            var messages = chat.Messages.ToArray();
            for (var i = 0; i < messages.Length; i++)
            {
                store.SaveMessage(chat, messages[i]);
                if (i % 8 == 7) await Task.Yield();
            }
            await Task.Yield();
        }
    }
    private async Task SavePendingAsync()
    {
        var pending = pendingSaves.ToArray(); pendingSaves.Clear();
        try
        {
            foreach (var chat in pending)
            {
                if (closing || !chats.Contains(chat) || runtimes.GetValueOrDefault(chat.Id)?.IsLoadingHistory == true) continue;
                store.Save(chat);
                var messages = chat.Messages.ToArray();
                for (var i = 0; i < messages.Length; i++)
                {
                    store.SaveMessage(chat, messages[i]);
                    if (i % 8 == 7) await Task.Yield();
                }
                await Task.Yield();
            }
        }
        catch { pendingSaves.UnionWith(pending); throw; }
    }
    private async Task BrowseHistory(bool newer)
    {
        if (current is not { } chat) return;
        pageLoad?.Cancel(); var cancellation = pageLoad = CancellationTokenSource.CreateLinkedTokenSource(discoveryLifetime.Token);
        var visible = MessageList.Items.OfType<Message>().ToArray(); if (visible.Length == 0) return;
        try
        {
            var page = await store.ReadPageAsync(chat, newer ? visible[^1].Sequence : visible[0].Sequence, limit: 50, token: cancellation.Token, newer: newer);
            if (cancellation.IsCancellationRequested || current != chat || page.Length == 0) return;
            var merged = HistoryWindow.Navigate(visible, page, newer);
            viewingHistory = true;
            TranscriptNavigation.ReplacePage(MessageList, merged); UpdateHistoryNavigation(); UpdateComposerAction();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Could not load history: " + error.Message; }
    }
    private async void LatestMessagesClick(object? sender, RoutedEventArgs e)
    {
        pageLoad?.Cancel(); viewingHistory = false; MessageList.ItemsSource = current?.Messages;
        UpdateComposerAction();
        try { if (current is { HistoryLoaded: false } chat) await RestoreVisibleHistory(chat, discoveryLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Could not reload history: " + error.Message; }
        ScrollTranscriptToEnd(force: true);
    }
    private bool transcriptScrollPending;
    private void ScrollTranscriptToEnd(bool force = false)
    {
        if (uiSleeping || !force && transcriptScrollPending) return;
        var initialize = force && MessageList.ItemsPanelRoot is not TranscriptPanel;
        if (force && MessageList.ItemsPanelRoot is TranscriptPanel followingPanel) followingPanel.FollowEnd();
        transcriptScrollPending = true;
        var chat = current;
        Dispatcher.UIThread.Post(() =>
        {
            transcriptScrollPending = false;
            if (closing || viewingHistory || !ReferenceEquals(current, chat) ||
                (!force && chat is not null && runtimes.TryGetValue(chat.Id, out var runtime) && runtime.IsLoadingHistory)) return;
            if (!initialize && MessageList.ItemsPanelRoot is not TranscriptPanel { IsFollowingEnd: true }) return;
            if (initialize && MessageList.ItemsPanelRoot is TranscriptPanel panel) panel.FollowEnd();
            MessageList.ScrollIntoView(MessageList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
    private void ClearChat()
    {
        ChatSearch.Close();
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
            item.Click += (_, _) => ShowArchiveView(archived);
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
        var dialog = new Window { Title = "Delete chat", Width = 530, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var confirm = new Button { Name = "ConfirmDeleteButton", Content = "Delete" }; var cancel = new Button { Content = "Cancel" };
        confirm.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = { new TextBlock { Text = "Delete “" + chat.Title + "”?", FontSize = 20, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = "Chats and attachments will be removed from this app. We will also try to delete provider history, which may also remove child sessions or session worktrees.", TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } } } };
        if (!await dialog.ShowDialog<bool>(desktopWindow!)) return;
        historyOperation = DeleteChat(chat, owner); await historyOperation;
    }
    private async Task DeleteChat(Chat chat, Workspace owner)
    {
        try { var warning = await DeleteChatCore(chat, owner); StatusText.Text = "Chat deleted." + (warning is null ? "" : " Provider history cleanup: " + warning); }
        catch (Exception error) { StatusText.Text = "Could not delete chat: " + error.Message; }
    }
    private async Task<string?> DeleteChatCore(Chat chat, Workspace owner)
    {
        if (chat.IsDeleting || chat.Busy) throw new IOException("Stop the chat before deleting it.");
        if (ReferenceEquals(current, chat)) { pageLoad?.Cancel(); ClearChat(); }
        try
        {
            return await ChatDeletion.Delete(store, chats, chat, owner, async () =>
            {
                if (runtimes.Remove(chat.Id, out var runtime)) await runtime.DisposeAsync();
            });
        }
        finally { RefreshChats(); UpdateControls(); }
    }
    private async Task<string?> DeleteRemoteChat(Chat chat, Workspace owner)
    {
        var operation = DeleteChatCore(chat, owner); workspaceClosures.Add(operation);
        try { return await operation; }
        finally { workspaceClosures.Remove(operation); }
    }
    private bool TrayAvailable => tray?.IsVisible == true && store.Setting("runInTray") != "0" && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || tray.NativeMenuExporter is not null);
    private void ConfigureTray()
    {
        if (remoteOnly || desktopWindow is null) return;
        // Linux's asynchronous D-Bus watcher can throw on cancellation during disposal.
        // Keep one icon for the window lifetime and toggle visibility instead.
        if (store.Setting("runInTray") == "0") { if (tray is not null) tray.IsVisible = false; return; }
        if (tray is not null) { tray.IsVisible = true; UpdateTray(); return; }
        var show = new NativeMenuItem("Open Vibe Harder"); show.Click += (_, _) => ShowFromTray();
        var quit = new NativeMenuItem("Quit and interrupt agents"); quit.Click += TrayQuitClick;
        tray = new TrayIcon { Icon = desktopWindow.Icon, ToolTipText = "Vibe Harder — no agents running", IsVisible = true, Menu = new NativeMenu { Items = { show, quit } } };
        UpdateTray();
        tray.Clicked += (_, _) => ShowFromTray();
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app);
            if (icons is null) { icons = new TrayIcons(); TrayIcon.SetIcons(app, icons); }
            icons.Add(tray);
        }
    }
    private void ReleaseTray()
    {
        var previous = tray; tray = null; trayState = "";
        if (previous is null) return;
        // Avalonia disposes registered icons when they are removed. Do not dispose twice.
        var icons = Application.Current is { } app ? TrayIcon.GetIcons(app) : null;
        if (icons?.Contains(previous) == true) icons.Remove(previous);
        else previous.Dispose();
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
        var quit = new NativeMenuItem(active.Length == 0 ? "Quit" : "Quit and interrupt agents"); quit.Click += TrayQuitClick; menu.Items.Add(quit);
        tray.Menu = menu;
    }
    public void ShowFromTray() { if (closing) return; foregroundRequested = true; desktopWindow?.Show(); if (desktopWindow is { } window) { window.WindowState = WindowState.Normal; window.Activate(); } UpdateControls(); }
    private Window? quitConfirmation;
    private async void TrayQuitClick(object? sender, EventArgs e)
    {
        try { await ConfirmTrayExit(); }
        catch (Exception error) { StatusText.Text = AppDiagnostics.Message("Could not confirm quit", error); }
    }
    private async Task ConfirmTrayExit()
    {
        if (closing || desktopWindow is null) return;
        if (quitConfirmation is { } existing) { existing.Activate(); return; }
        ShowFromTray();
        var dialog = new Window { Title = "Quit Vibe Harder?", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        quitConfirmation = dialog;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Quitting will stop all agents and close all terminal sessions and their subprocesses running in this app. Any work still running will be interrupted.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Name = "CancelTrayQuit", Content = "Cancel" };
        var quit = new Button { Name = "ConfirmTrayQuit", Content = "Quit and stop everything", Classes = { "accent" } };
        cancel.Click += (_, _) => dialog.Close(false);
        quit.Click += (_, _) => dialog.Close(true);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(false); } };
        dialog.Opened += (_, _) => cancel.Focus();
        buttons.Children.Add(cancel); buttons.Children.Add(quit); panel.Children.Add(buttons); dialog.Content = panel;
        try { if (await dialog.ShowDialog<bool>(desktopWindow) && !closing) RequestExit(); }
        finally { quitConfirmation = null; }
    }
    public void RequestExit() { exitRequested = true; desktopWindow?.Close(); }
    private async Task OfferInterruptedChats()
    {
        if (recoveryOffered) return; recoveryOffered = true;
        var updateResume = DesktopUpdater.ConsumeResumeChats(store, Environment.GetCommandLineArgs().Contains("--updated"));
        var interrupted = chats.Where(c => c.InterruptedInput is not null).ToArray();
        if (interrupted.Length == 0 || closing) return;
        if (store.Setting("autoResume") == "1" || updateResume.Count > 0)
        {
            foreach (var chat in interrupted)
            {
                if (store.Setting("autoResume") != "1" && !updateResume.Contains(chat.Id)) continue;
                var owner = store.Workspaces().FirstOrDefault(w => w.Id == chat.WorkspaceId);
                if (owner is null) continue;
                if (!workspaces.Any(w => w.Id == owner.Id)) { workspaces.Add(owner); store.Setting("closed:" + owner.Id, "0"); BuildWorkspaceTree(); }
                _ = ResumeInterrupted(chat, owner, chat.InterruptedInput!, updateResume.Contains(chat.Id), automatic: true);
            }
            return;
        }
        var dialog = new Window { Title = "Resume interrupted chats", Width = 520, Height = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "These chats were interrupted when the app exited. Resume selected chats from their saved history?", TextWrapping = TextWrapping.Wrap });
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
    private async Task ResumeInterrupted(Chat chat, Workspace owner, PendingInput input, bool afterUpdate = false, bool automatic = false)
    {
        var runtime = Runtime(chat, owner);
        while (runtime.IsReconnecting || runtime.IsLoadingHistory) { if (closing) return; await Task.Delay(50); }
        if (chat.Busy || closing) return;
        while (!runtime.IsConnected && !closing)
        {
            await runtime.Reconnect();
            if (closing) return;
            if (runtime.IsConnected) break;
            if (chat.NeedsLogin || (!afterUpdate && store.Setting("autoResume") != "1")) return;
            chat.Status = "Waiting to reconnect before automatic resume…"; UpdateControls();
            try { await Task.Delay(TimeSpan.FromSeconds(15), discoveryLifetime.Token); } catch (OperationCanceledException) { return; }
        }
        if (closing || chat.Busy) return;
        chat.InterruptedInput = null;
        if (chat.Draft == input.Text) { chat.Draft = ""; if (ReferenceEquals(current, chat)) Composer.Text = ""; }
        foreach (var attachment in input.Attachments) chat.Attachments.Remove(attachment);
        await runtime.Send(" ", [], autoResume: automatic);
    }
    private bool shutdownComplete;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!closing && !exitRequested && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined && store.Setting("runInTray") != "0" && TrayAvailable)
        {
            e.Cancel = true;
            try { await SavePendingAsync(); await store.FlushAsync(); desktopWindow?.Hide(); }
            catch (Exception error) { StatusText.Text = AppDiagnostics.Message("Could not save before hiding", error); }
            return;
        }
        if (closing) { e.Cancel = !shutdownComplete; return; }
        AppDiagnostics.RecoveryRequested -= RecoverAfterError;
        if (restartingForUpdate) store.Setting("updateResume", string.Join('\n', chats.Where(c => c.Busy).Select(c => c.Id)));
        e.Cancel = true; closing = true; DisposePresentationSleep(); discoveryLifetime.Cancel(); saveTimer.Stop();
        settingsWindow?.Close();
        var errors = new List<Exception>();
        async Task Cleanup(Func<Task> action)
        {
            try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { errors.Add(error); }
        }
        try
        {
            await Cleanup(SaveAllAsync);
            await Cleanup(() => backendUpdates);
            CloseRemoteView();
            remoteSessions.Dispose();
            // Cancel providers before waiting for operations that depend on them.
            var stoppingAgents = runtimes.Values.Select(runtime => Cleanup(() => runtime.DisposeAsync().AsTask())).ToArray();
            foreach (var login in loginSessions.Values) await Cleanup(() => Task.Run(login.Session.Dispose));
            foreach (var terminal in terminals.Values.SelectMany(t => t)) await Cleanup(() => Task.Run(terminal.Session.Dispose));
            if (remoteServer is not null) await Cleanup(() => remoteServer.DisposeAsync().AsTask());
#if !MOBILE_CLIENT
            await Cleanup(async () =>
            {
                await webConfiguration.WaitAsync();
                try { if (webServer is not null) await webServer.DisposeAsync(); }
                finally { webConfiguration.Release(); }
            });
#endif
            await Task.WhenAll(stoppingAgents);
            await Cleanup(() => Task.WhenAll(discoveries.Values));
            await Cleanup(() => Task.WhenAll(workspaceClosures.ToArray()));
            if (historyOperation is not null) await Cleanup(() => historyOperation);
            await Cleanup(async () => { await SaveAllAsync(); await store.FlushAsync(); });
            await Cleanup(() => Task.Run(store.Dispose));
        }
        finally
        {
            ReleaseTray();
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




