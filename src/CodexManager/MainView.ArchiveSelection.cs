using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private sealed record ArchivedSelection(Chat Chat, RemoteView? Remote);
    private readonly Dictionary<string, ArchivedSelection> archivedSelection = [];
    private bool archiveBatchRunning;
    private readonly HashSet<string> expandedArchives = [];
    private bool WorkspaceExpanded(string key) => showArchived ? expandedArchives.Contains(key) : store.Setting("collapsed:" + key) != "1";
    private void SetWorkspaceExpanded(string key, bool expanded)
    {
        if (showArchived) { if (expanded) expandedArchives.Add(key); else expandedArchives.Remove(key); }
        else store.Setting("collapsed:" + key, expanded ? "0" : "1");
    }
    private void ShowArchiveView(bool archived)
    {
        showArchived = archived; archivedSelection.Clear(); expandedArchives.Clear();
        ArchiveViewButton.Content = AppIcons.Label("chevron-down", archived ? "Archived" : "Chats", trailing: true);
        SearchBox.Text = ""; BuildWorkspaceTree();
    }

    private Control ArchiveSelectableRow(Control content, Chat chat, string key, RemoteView? remote)
    {
        var check = new CheckBox { Name = "SelectArchived_" + chat.Id, IsChecked = archivedSelection.ContainsKey(key), IsEnabled = !archiveBatchRunning, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Avalonia.Automation.AutomationProperties.SetName(check, "Select " + chat.Title);
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true) archivedSelection[key] = new(chat, remote); else archivedSelection.Remove(key);
            UpdateArchiveSelection();
        };
        var row = new Grid { ColumnDefinitions = new("Auto,*") };
        row.Children.Add(check); Grid.SetColumn(content, 1); row.Children.Add(content); return row;
    }
    private void UpdateArchiveSelection()
    {
        foreach (var key in archivedSelection.Where(p => !p.Value.Chat.Archived || p.Value.Remote is null && !chats.Contains(p.Value.Chat)).Select(p => p.Key).ToArray()) archivedSelection.Remove(key);
        ArchiveSelectionBar.IsVisible = showArchived;
        ArchiveSelectionBar.IsEnabled = !archiveBatchRunning;
        ArchiveSelectionCount.Text = archivedSelection.Count == 0 ? "Select chats" : $"{archivedSelection.Count} selected";
        RestoreSelectedChats.IsEnabled = DeleteSelectedChats.IsEnabled = ClearArchiveSelection.IsEnabled = archivedSelection.Count > 0;
    }
    private void ClearArchiveSelectionClick(object? sender, RoutedEventArgs e)
    {
        archivedSelection.Clear(); UpdateArchiveSelection(); BuildWorkspaceTree();
    }
    private async void RestoreSelectedChatsClick(object? sender, RoutedEventArgs e)
    {
        historyOperation = ApplyArchivedSelection(false, archivedSelection.ToArray()); await historyOperation;
    }
    private void DeleteSelectedChatsClick(object? sender, RoutedEventArgs e)
    {
        if (archiveBatchRunning || archivedSelection.Count == 0) return;
        var selected = archivedSelection.ToArray();
        var popup = new Flyout();
        var confirm = new Button { Name = "ConfirmBulkDelete", Content = "Delete selected" };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => popup.Hide();
        confirm.Click += async (_, _) => { popup.Hide(); historyOperation = ApplyArchivedSelection(true, selected); await historyOperation; };
        popup.Content = new StackPanel { Width = Math.Min(320, Math.Max(220, Bounds.Width - 40)), Spacing = 12, Children =
        {
            new TextBlock { Text = $"Delete {selected.Length} selected chats?", FontSize = 16, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Chats and attachments will be removed from this app. We'll also try to delete their provider history, which may also remove child sessions or session worktrees.", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, confirm } }
        } };
        popup.ShowAt(DeleteSelectedChats);
    }
    private async Task ApplyArchivedSelection(bool delete, KeyValuePair<string, ArchivedSelection>[] selected)
    {
        if (archiveBatchRunning) return;
        archiveBatchRunning = true; UpdateArchiveSelection();
        var completed = 0; List<string> warnings = [];
        try
        {
            foreach (var (key, target) in selected)
            {
                try
                {
                    if (!target.Chat.Archived) { archivedSelection.Remove(key); continue; }
                    if (delete)
                    {
                        var warning = target.Remote is { } remote ? await remote.DeleteArchivedChat(target.Chat.Id) :
                            await DeleteChatCore(target.Chat, workspaces.Single(w => w.Id == target.Chat.WorkspaceId));
                        if (warning is not null) warnings.Add(target.Chat.Title + ": " + warning);
                    }
                    else if (target.Remote is { } remote) await remote.UnarchiveChat(target.Chat.Id);
                    else { target.Chat.Archived = false; store.Save(target.Chat); }
                    archivedSelection.Remove(key); completed++;
                }
                catch (Exception error) { warnings.Add(target.Chat.Title + ": " + error.Message); }
            }
            await store.FlushAsync();
        }
        catch (Exception error) { warnings.Add(error.Message); }
        finally
        {
            archiveBatchRunning = false; BuildWorkspaceTree(); UpdateControls(); UpdateArchiveSelection();
            StatusText.Text = $"{completed} chats {(delete ? "deleted" : "unarchived")}." + (warnings.Count == 0 ? "" : $" {warnings.Count} cleanup or operation issues: " + string.Join("; ", warnings));
            ToolTip.SetTip(StatusText, StatusText.Text);
        }
    }
}
