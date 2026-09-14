using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class WorkspaceSelector : StackPanel
{
    private IReadOnlyList<Workspace> entries;
    private readonly TextBox search = new() { Name = "WorkspaceHistorySearch", PlaceholderText = "Search workspace history…" };
    private readonly ListBox list = new() { Name = "WorkspaceHistoryList", MaxHeight = 380, Background = Brushes.Transparent };
    public event Action<Workspace>? Chosen;
    public event Action<Workspace>? Removed;
    public event Action? Browse;
    public WorkspaceSelector(IReadOnlyList<Workspace> history, bool allowRemoval = true, string? remotePlatform = null)
    {
        if (OperatingSystem.IsAndroid()) ScrollViewer.SetVerticalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden);
        entries = history; HorizontalAlignment = HorizontalAlignment.Stretch; Spacing = 6;
        list.Padding = new Thickness(0);
        Children.Add(search); Children.Add(list);
        var open = new Button { Name = "BrowseWorkspaceHistory", Content = AppIcons.Label("add", "Open folder…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += (_, _) => Browse?.Invoke(); Children.Add(open);
        list.ItemTemplate = new FuncDataTemplate<Workspace>((workspace, _) =>
        {
            if (workspace is null) return null;
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            var label = new DockPanel(); var icon = PlatformIcons.For(workspace, remotePlatform); DockPanel.SetDock(icon, Dock.Left); label.Children.Add(icon);
            label.Children.Add(new TextBlock { Text = workspace.Name + (workspace.IsWsl ? $" ({workspace.Distro})" : ""), TextTrimming = TextTrimming.CharacterEllipsis });
            var choose = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(choose, workspace.Path); choose.Click += (_, _) => Chosen?.Invoke(workspace);
            var remove = new IconButton { Name = "RemoveHistory_" + workspace.Id, Icon = "remove", IconSize = 10, Label = "Remove from history (keep saved chats)", Padding = new Thickness(5, 2) };
            remove.IsVisible = allowRemoval;
            ToolTip.SetTip(remove, "Remove from history (keep saved chats)");
            remove.Click += (_, _) => Removed?.Invoke(workspace);
            Grid.SetColumn(remove, 1); row.Children.Add(choose); row.Children.Add(remove); return row;
        });
        search.TextChanged += (_, _) => Filter();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is Workspace workspace) { e.Handled = true; Chosen?.Invoke(workspace); }
            else if (e.Key is Key.Down or Key.Up) { e.Handled = true; list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, list.ItemCount - 1)); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Filter();
    }
    public void Refresh(IReadOnlyList<Workspace> history) { entries = history; Filter(); }
    public void FocusSearch() => search.Focus();
    private void Filter()
    {
        var query = search.Text ?? "";
        list.ItemsSource = entries.Where(w => (w.Name + " " + w.Path + " " + w.Distro).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        list.SelectedIndex = list.ItemCount > 0 ? 0 : -1;
    }
}
