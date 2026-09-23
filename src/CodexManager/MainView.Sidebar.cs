using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CodexManager;

public partial class MainView
{
    private readonly Dictionary<RemoteHost, JsonNode> remoteCatalogs = [];
    private readonly Dictionary<string, (StackPanel Group, ListBox List, IconButton Collapse, string Signature)> remoteWorkspaceVisuals = [];
    private bool applyingRemoteSidebar;
    private static string RemoteCollapsedKey(RemoteHost host) => "collapsed:connection:" + host.Address + ":" + host.Port;
    private Control SidebarChatRow(Chat chat, Action<IconButton> renameChat, Func<Task> archiveChat, string? scope = null, string? colorId = null, RemoteView? remote = null)
    {
        var row = new Grid { ColumnDefinitions = new("20,*,Auto,Auto"), Margin = new(0, 4), Background = Brushes.Transparent, Classes = { "chatRow" } };
        row.Tapped += (_, e) =>
        {
            if (!chat.Archived && e.Source is Visual source && source is not Button && !source.GetVisualAncestors().TakeWhile(v => v != row).OfType<Button>().Any()) CollapseSidebar();
        };
        if (!chat.Archived) EnableHoldReorder(row, scope ?? "chats:" + chat.WorkspaceId, chat.Id, () => { RefreshChats(); RefreshRemoteSidebar(); },
            move: remote is null ? null : (source, target, after) => _ = remote.Reorder("chats:" + chat.WorkspaceId, source, target, after));
        ColorMenu(row, "chatColor:" + (colorId ?? chat.Id), "Chat input border color", () => { ApplyChatColors(); foreach (var view in remoteViews.Values) view.ApplyColors(); });
        row.Children.Add(new ChatActivityIndicator(chat) { Name = "Activity_" + chat.Id, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 0, 0) });
        var details = new StackPanel { Spacing = 3 };
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, CompiledBinding.Create((Chat c) => c.Title, source: chat));
        var state = new TextBlock { FontSize = 11, Classes = { "muted" } };
        state.Bind(TextBlock.TextProperty, CompiledBinding.Create((Chat c) => c.Status, source: chat));
        details.Children.Add(title); details.Children.Add(state); Grid.SetColumn(details, 1); row.Children.Add(details);
        var rename = new IconButton { Name = "Rename_" + chat.Id, Icon = "edit", Label = "Rename chat", MinWidth = OperatingSystem.IsAndroid() ? 40 : 20, MinHeight = OperatingSystem.IsAndroid() ? 40 : 20, VerticalAlignment = VerticalAlignment.Top, Classes = { "rowAction" } };
        rename.Click += (_, e) => { e.Handled = true; renameChat(rename); }; Grid.SetColumn(rename, 2); row.Children.Add(rename);
        var archive = new IconButton { Name = "Archive_" + chat.Id, Icon = "archive", Label = chat.Archived ? "Restore chat" : "Archive chat", MinWidth = OperatingSystem.IsAndroid() ? 40 : 20, MinHeight = OperatingSystem.IsAndroid() ? 40 : 20, VerticalAlignment = VerticalAlignment.Top, Classes = { "rowAction" } };
        archive.Click += async (_, e) => { e.Handled = true; await archiveChat(); }; Grid.SetColumn(archive, 3); row.Children.Add(archive);
        ToolTip.SetTip(row, chat.ProviderLabel); return chat.Archived ? ArchiveSelectableRow(row, chat, colorId ?? chat.Id, remote) : row;
    }
    private void RefreshRemoteSidebar()
    {
        if (sidebarDragging || sidebarHolding) return;
        if (remoteView is { } active) ImportChatsButton.IsEnabled = active.HasWorkspace;
        foreach (var (remoteHost, catalog) in remoteCatalogs)
        {
            if (!remoteViews.TryGetValue(remoteHost, out var view) || !remoteSections.TryGetValue(remoteHost.Address + ":" + remoteHost.Port, out var section)) continue;
            if (store.Setting(RemoteCollapsedKey(remoteHost)) == "1") continue;
            var host = view.Host;
            var desired = new List<Control> { section.Children.FirstOrDefault() ?? view.CreateConnectionStatus() };
            var retained = new HashSet<string>();
            var connectionScope = "remote:" + host.Address + ":" + host.Port + ":";
            var records = catalog["chats"]!.AsArray().OfType<JsonNode>().ToArray();
            var byWorkspace = records.Where(c => c["archived"]!.GetValue<bool>() == showArchived).ToLookup(c => c["workspaceId"]!.GetValue<string>());
            var chatKeys = records.Select(c => connectionScope + c["id"]!.GetValue<string>()).ToHashSet();
            foreach (var stale in remoteActivity.Keys.Where(k => k.StartsWith(connectionScope, StringComparison.Ordinal) && !chatKeys.Contains(k)).ToArray()) remoteActivity.Remove(stale);
            foreach (var workspace in catalog["workspaces"]!.AsArray().OfType<JsonNode>())
            {
                var ownerId = workspace!["id"]!.GetValue<string>(); var key = "remote:" + host.Address + ":" + ownerId;
                var rows = new List<Chat>();
                foreach (var record in byWorkspace[ownerId])
                {
                    var id = record["id"]!.GetValue<string>(); var activityKey = connectionScope + id;
                    if (!remoteActivity.TryGetValue(activityKey, out var chat)) remoteActivity[activityKey] = chat = new Chat { Id = id, WorkspaceId = ownerId, Provider = Enum.Parse<AgentProvider>(record["provider"]!.GetValue<string>()) };
                    if (DateTimeOffset.TryParse(record["updated"]?.GetValue<string>(), out var updated)) chat.Updated = updated;
                    chat.Title = record["title"]!.GetValue<string>(); chat.Status = record["status"]!.GetValue<string>(); chat.Busy = record["busy"]!.GetValue<bool>(); chat.Archived = showArchived; chat.HasUnreadCompletion = record["unread"]?.GetValue<bool>() == true; if (chat.HasUnreadCompletion && !chat.Busy) chat.Status = "Done";
                    chat.NeedsPermission = record["needsPermission"]?.GetValue<bool>() == true;
                    if (chat.Title.Contains(SearchBox.Text ?? "", StringComparison.OrdinalIgnoreCase)) rows.Add(chat);
                }
                var scope = "remote:" + host.Address + ":" + host.Port + ":";
                var visualKey = connectionScope + ownerId;
                retained.Add(visualKey);
                var signature = workspace["name"] + "\0" + workspace["distro"] + "\0" + showArchived + "\0" + store.Setting("workspaceColor:" + scope + ownerId);
                if (remoteWorkspaceVisuals.TryGetValue(visualKey, out var cached) && cached.Signature == signature)
                {
                    applyingRemoteSidebar = true;
                    try
                    {
                        if (cached.List.ItemsSource is not IEnumerable<Chat> previous || !previous.SequenceEqual(rows)) cached.List.ItemsSource = rows;
                        cached.List.IsVisible = WorkspaceExpanded(key);
                        cached.Collapse.Icon = cached.List.IsVisible ? "chevron-down" : "chevron-right";
                        cached.List.SelectedItem = ReferenceEquals(remoteView, view) ? rows.FirstOrDefault(c => c.Id == view.SelectedChatId) : null;
                    }
                    finally { applyingRemoteSidebar = false; }
                    desired.Add(cached.Group); continue;
                }
                var list = new SidebarChatList { Name = "Chats_remote_" + ownerId, ItemsSource = rows, Background = Brushes.Transparent, Margin = new(8, 0, 0, 0), IsVisible = WorkspaceExpanded(key) };
                list.ItemTemplate = new FuncDataTemplate<Chat>((chat, _) => chat is null ? null : SidebarChatRow(chat, anchor => view.RenameChat(anchor, chat.Id, chat.Title), () => view.ArchiveChat(chat.Id, !chat.Archived), "chats:" + scope + ownerId, scope + chat.Id, view));
                list.SelectedItem = ReferenceEquals(remoteView, view) ? rows.FirstOrDefault(c => c.Id == view.SelectedChatId) : null;
                list.SelectionChanged += (_, _) => { if (!applyingRemoteSidebar && !showArchived && list.SelectedItem is Chat { Archived: false } chat) { OpenRemoteHost(host); chat.HasUnreadCompletion = false; view.SelectChat(chat.Id); RefreshRemoteSidebar(); } };
                var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto") };
                var collapse = new IconButton { Icon = list.IsVisible ? "chevron-down" : "chevron-right", Label = "Collapse or expand workspace" };
                collapse.Click += (_, _) => { list.IsVisible = !list.IsVisible; collapse.Icon = list.IsVisible ? "chevron-down" : "chevron-right"; SetWorkspaceExpanded(key, list.IsVisible); }; header.Children.Add(collapse);
                var title = new Button { Content = workspace["name"]!.GetValue<string>() + (workspace["distro"]?.GetValue<string>() is { } distro ? " · " + distro + " (WSL)" : " · Files on " + host.Name), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                title.Click += (_, _) => { if (showArchived) { SetWorkspaceExpanded(key, !WorkspaceExpanded(key)); RefreshRemoteSidebar(); } else OpenRemoteHost(host, ownerId); }; Grid.SetColumn(title, 1); header.Children.Add(title);
                var create = new IconButton { Icon = "add", IconSize = 10, Label = "New chat", IsVisible = !showArchived, Classes = { "rowAction" } }; create.Click += (_, _) => view.ShowNewChat(create, ownerId, () => OpenRemoteHost(host)); Grid.SetColumn(create, 2); header.Children.Add(create);
                var close = new IconButton { Icon = "remove", IconSize = 10, Label = "Close workspace (keep chats)", Classes = { "rowAction" } }; close.Click += async (_, _) => await view.CloseWorkspace(ownerId); Grid.SetColumn(close, 3); header.Children.Add(close);
                var group = new StackPanel { Background = SidebarColors.Brush(store, "workspaceColor:" + scope + ownerId, true) };
                var heading = new Grid { ColumnDefinitions = new("*,Auto"), Background = Brushes.Transparent, Classes = { "workspaceHeading" } };
                EnableHoldReorder(group, "workspaces:" + connectionScope, ownerId, RefreshRemoteSidebar, heading,
                    (source, target, after) => _ = view.Reorder("workspaces", source, target, after)); heading.Children.Add(header);
                group.Children.Add(heading); group.Children.Add(list); group.Children.Add(WorkspaceDivider());
                ColorMenu(title, "workspaceColor:" + scope + ownerId, "Workspace background color", () => { RefreshRemoteSidebar(); view.ApplyColors(); });
                remoteWorkspaceVisuals[visualKey] = (group, list, collapse, signature);
                desired.Add(group);
            }
            if (!section.Children.SequenceEqual(desired))
            {
                section.Children.Clear();
                foreach (var child in desired) section.Children.Add(child);
            }
            foreach (var key in remoteWorkspaceVisuals.Keys.Where(k => k.StartsWith(connectionScope, StringComparison.Ordinal) && !retained.Contains(k)).ToArray()) remoteWorkspaceVisuals.Remove(key);
        }
    }
    private void ShowMobilePalette()
    {
        var popup = new Flyout(); var query = new TextBox { PlaceholderText = "Search commands…" }; var results = new StackPanel();
        var commands = new (string Title, Action Run)[] {
            ("Open workspace", () => WorkspaceSelectorClick(this, new())),
            ("Settings", () => SettingsClick(this, new())),
            ("Remote terminal", () => MobileTerminalClick(this, new())),
            ("Reconnect", () => remoteView?.ReconnectHost()) };
        void Filter() { results.Children.Clear(); foreach (var command in commands.Where(c => c.Title.Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase))) { var button = new Button { Content = command.Title, HorizontalAlignment = HorizontalAlignment.Stretch }; button.Click += (_, _) => { popup.Hide(); command.Run(); }; results.Children.Add(button); } }
        query.TextChanged += (_, _) => Filter(); Filter();
        popup.Content = new StackPanel { Width = Math.Min(330, Math.Max(220, Bounds.Width - 32)), Spacing = 8, Children = { query, results } }; popup.ShowAt(CommandPaletteButton); query.Focus();
    }
}
