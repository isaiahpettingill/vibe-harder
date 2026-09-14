using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private readonly Dictionary<RemoteHost, JsonNode> remoteCatalogs = [];
    private Control SidebarChatRow(Chat chat, Action<IconButton> renameChat, Func<Task> archiveChat)
    {
        var row = new Grid { ColumnDefinitions = new("20,*,Auto,Auto"), Margin = new(0, 4), Background = Brushes.Transparent, Classes = { "chatRow" } };
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
        if (OperatingSystem.IsAndroid()) { rename.Opacity = 1; archive.Opacity = 1; }
        ToolTip.SetTip(row, chat.ProviderLabel); return row;
    }
    private void RefreshRemoteSidebar()
    {
        if (remoteView is { } active) ImportChatsButton.IsEnabled = active.HasWorkspace;
        foreach (var (remoteHost, catalog) in remoteCatalogs)
        {
            if (!remoteViews.TryGetValue(remoteHost, out var view) || !remoteSections.TryGetValue(remoteHost.Address + ":" + remoteHost.Port, out var section)) continue;
            var host = view.Host; section.Children.Clear(); section.Children.Add(view.CreateConnectionStatus());
            foreach (var workspace in catalog["workspaces"]!.AsArray())
            {
                var ownerId = workspace!["id"]!.GetValue<string>(); var key = "remote:" + host.Address + ":" + ownerId;
                if (store.Setting("closed:" + key) == "1") continue;
                var rows = new List<Chat>();
                foreach (var record in catalog["chats"]!.AsArray().Where(c => c!["workspaceId"]!.GetValue<string>() == ownerId && c["archived"]!.GetValue<bool>() == showArchived))
                {
                    var id = record!["id"]!.GetValue<string>(); var activityKey = host.Address + ":" + id;
                    if (!remoteActivity.TryGetValue(activityKey, out var chat)) remoteActivity[activityKey] = chat = new Chat { Id = id, WorkspaceId = ownerId, Provider = Enum.Parse<AgentProvider>(record["provider"]!.GetValue<string>()) };
                    chat.Title = record["title"]!.GetValue<string>(); chat.Status = record["status"]!.GetValue<string>(); chat.Busy = record["busy"]!.GetValue<bool>(); chat.Archived = showArchived; chat.HasUnreadCompletion = record["unread"]?.GetValue<bool>() == true; if (chat.HasUnreadCompletion && !chat.Busy) chat.Status = "Done";
                    chat.NeedsPermission = record["needsPermission"]?.GetValue<bool>() == true;
                    if (chat.Title.Contains(SearchBox.Text ?? "", StringComparison.OrdinalIgnoreCase)) rows.Add(chat);
                }
                var list = new ListBox { Name = "Chats_remote_" + ownerId, ItemsSource = rows, Background = Brushes.Transparent, Margin = new(8, 0, 0, 0), IsVisible = store.Setting("collapsed:" + key) != "1" };
                list.ItemTemplate = new FuncDataTemplate<Chat>((chat, _) => chat is null ? null : SidebarChatRow(chat, anchor => view.RenameChat(anchor, chat.Id, chat.Title), () => view.ArchiveChat(chat.Id, !chat.Archived)));
                list.SelectedItem = ReferenceEquals(remoteView, view) ? rows.FirstOrDefault(c => c.Id == view.SelectedChatId) : null;
                list.SelectionChanged += (_, _) => { if (list.SelectedItem is Chat chat) { OpenRemoteHost(host); chat.HasUnreadCompletion = false; view.SelectChat(chat.Id); RefreshRemoteSidebar(); } };
                var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto") };
                var collapse = new IconButton { Icon = list.IsVisible ? "chevron-down" : "chevron-right", Label = "Collapse or expand workspace" };
                collapse.Click += (_, _) => { list.IsVisible = !list.IsVisible; collapse.Icon = list.IsVisible ? "chevron-down" : "chevron-right"; store.Setting("collapsed:" + key, list.IsVisible ? "0" : "1"); }; header.Children.Add(collapse);
                var title = new Button { Content = workspace["name"]!.GetValue<string>() + (workspace["distro"]?.GetValue<string>() is { } distro ? " · " + distro + " (WSL)" : " · Files on " + host.Name), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                title.Click += (_, _) => { OpenRemoteHost(host, ownerId); }; Grid.SetColumn(title, 1); header.Children.Add(title);
                var create = new IconButton { Icon = "add", Label = "New chat" }; create.Click += (_, _) => { OpenRemoteHost(host); view.ShowNewChat(OpenWorkspaceButton, ownerId); }; Grid.SetColumn(create, 2); header.Children.Add(create);
                var close = new IconButton { Icon = "remove", Label = "Close workspace (keep chats)" }; close.Click += (_, _) => { store.Setting("closed:" + key, "1"); RefreshRemoteSidebar(); }; Grid.SetColumn(close, 3); header.Children.Add(close);
                section.Children.Add(new StackPanel { Children = { header, list } });
            }
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
