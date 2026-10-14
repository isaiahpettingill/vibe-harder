using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;

namespace CodexManager;

public static class HistoryActions
{
    public static async Task<MenuFlyout> Show(Control anchor, Message? message, Func<JsonObject, Task<JsonNode?>> call, Action<string> selected)
    {
        var menu = new MenuFlyout();
        if (message is not null)
        {
            var copy = new MenuItem { Header = "Copy text" };
            copy.Click += async (_, _) => { try { if (TopLevel.GetTopLevel(anchor)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(message.Text); } catch (Exception error) { Error(anchor, error.Message); } };
            menu.Items.Add(copy);
            if (message.Attachments.Count > 0)
            {
                var files = new MenuItem { Header = "Show attachments" };
                files.Click += (_, _) =>
                {
                    var content = new StackPanel { Spacing = 8, MaxWidth = 640 };
                    foreach (var file in message.Attachments)
                        content.Children.Add(file.IsImage ? new AttachmentPreview { Attachment = file, IsExpanded = true } : new TextBlock { Text = file.Name, TextWrapping = TextWrapping.Wrap });
                    new Flyout { Content = new ScrollViewer { Content = content, MaxHeight = 420 } }.ShowAt(anchor);
                };
                menu.Items.Add(files);
            }
            menu.ShowAt(anchor);
        }
        try
        {
            var options = await call(new() { ["method"] = "history/options", ["messageId"] = message?.Id, ["sequence"] = message?.Sequence ?? -1 });
            if (options is null || TopLevel.GetTopLevel(anchor) is null) return menu;
            void Item(string title, bool fork, bool edit)
            {
                var item = new MenuItem { Header = title };
                item.Click += (_, _) => Edit(anchor, message, options["checkpoint"]?.GetValue<string>(), fork, edit, call, selected);
                menu.Items.Add(item);
            }
            if (message is null && options["fork"]?.GetValue<bool>() == true) Item("Fork chat…", true, false);
            else if (message?.Role == "user" && options["edit"]?.GetValue<bool>() == true) Item("Edit message…", false, true);
            else if (message is not null && options["point"]?.GetValue<bool>() == true)
            {
                Item("Fork from here…", true, false); Item("Revert to here…", false, false);
            }
            if (message is null && options["checkpoints"]?.GetValue<bool>() == true)
            {
                var checkpoints = new MenuItem { Header = "Restore checkpoint…" };
                checkpoints.Click += async (_, _) => await ShowCheckpoints(anchor, call, selected); menu.Items.Add(checkpoints);
            }
            if (message is null && menu.Items.Count > 0) menu.ShowAt(anchor);
        }
        catch (Exception error) { if (message is null) Error(anchor, error.Message); }
        return menu;
    }
    private static void Error(Control anchor, string text)
    {
        if (TopLevel.GetTopLevel(anchor) is not null) new Flyout { Content = new TextBlock { Text = text, MaxWidth = 360, TextWrapping = TextWrapping.Wrap } }.ShowAt(anchor);
    }
    private static async Task ShowCheckpoints(Control anchor, Func<JsonObject, Task<JsonNode?>> call, Action<string> selected)
    {
        try
        {
            var result = await call(new() { ["method"] = "checkpoints/list" });
            if (result?["checkpoints"] is not JsonArray checkpoints) throw new IOException("Could not load checkpoints. Try again.");
            var popup = new Flyout(); var rows = new StackPanel { Spacing = 6 };
            var notice = new TextBlock { Text = "Restores conversation and workspace files to the selected checkpoint. Later changes and queued messages will be discarded.", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            var choices = new ComboBox { Name = "CheckpointPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
            choices.ItemsSource = checkpoints.OfType<JsonObject>().Select(c => new CheckpointChoice(c["id"]!.GetValue<string>(),
                DateTimeOffset.TryParse(c["createdAt"]?.GetValue<string>(), out var date) ? date.ToLocalTime().ToString("g") + " · " + c["commitHash"]?.GetValue<string>()?[..Math.Min(8, c["commitHash"]!.GetValue<string>().Length)] : c["id"]!.GetValue<string>())).ToArray();
            choices.SelectedIndex = choices.ItemCount > 0 ? 0 : -1;
            var restore = new Button { Name = "RestoreCheckpoint", Content = "Restore checkpoint", IsEnabled = choices.ItemCount > 0 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            restore.Click += async (_, _) =>
            {
                if (choices.SelectedItem is not CheckpointChoice choice) return;
                restore.IsEnabled = false; choices.IsEnabled = false;
                try
                {
                    var response = await call(new() { ["method"] = "checkpoints/restore", ["checkpointId"] = choice.Id });
                    if (response?["id"]?.GetValue<string>() is not { } id) throw new IOException("Checkpoint restore did not complete. Check the connection before trying again.");
                    popup.Hide(); selected(id);
                }
                catch (Exception failure) { error.Text = failure.Message; }
                finally { restore.IsEnabled = true; choices.IsEnabled = true; }
            };
            rows.Children.Add(notice);
            if (choices.ItemCount == 0) rows.Children.Add(new TextBlock { Text = "No checkpoints yet." });
            else { rows.Children.Add(choices); rows.Children.Add(restore); }
            rows.Children.Add(error); popup.Content = rows; popup.ShowAt(anchor);
        }
        catch (Exception error) { Error(anchor, error.Message); }
    }
    private sealed record CheckpointChoice(string Id, string Label) { public override string ToString() => Label; }
    private static void Edit(Control anchor, Message? message, string? checkpoint, bool fork, bool edit, Func<JsonObject, Task<JsonNode?>> call, Action<string> selected)
    {
        var files = edit ? message!.Attachments.ToList() : [];
        var initial = edit ? message!.Text : "";
        var suffix = string.Concat(files.Select(file => "\n\n📎 " + file.Name));
        if (suffix.Length > 0 && initial.EndsWith(suffix, StringComparison.Ordinal)) initial = initial[..^suffix.Length];
        var popup = new Flyout();
        var input = new TextBox { Name = "HistoryMessageInput", Text = initial, PlaceholderText = edit ? "Edit your message" : "New message (optional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 260 };
        var createFork = new CheckBox { Name = "HistoryCreateFork", Content = "Fork into a new chat", IsChecked = fork, IsEnabled = message is not null };
        var attachments = new StackPanel();
        foreach (var file in files.ToArray())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = file.Name, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new IconButton { Icon = "remove", Label = "Remove " + file.Name }; remove.Click += (_, _) => { files.Remove(file); attachments.Children.Remove(row); }; row.Children.Add(remove); attachments.Children.Add(row);
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var apply = new Button { Name = "ApplyHistoryChange", Content = edit ? "Replace and send" : fork ? "Fork" : "Revert", Classes = { "accent" } };
        createFork.IsCheckedChanged += (_, _) => apply.Content = createFork.IsChecked == true ? edit ? "Fork and send" : "Fork" : edit ? "Replace and send" : "Revert";
        apply.Click += async (_, _) =>
        {
            if (edit && string.IsNullOrWhiteSpace(input.Text) && files.Count == 0) { error.Text = "Enter a message."; return; }
            apply.IsEnabled = false; input.IsEnabled = false; createFork.IsEnabled = false;
            try
            {
                var response = await call(new() { ["method"] = "history/branch", ["messageId"] = message?.Id, ["sequence"] = message?.Sequence ?? -1,
                    ["checkpoint"] = checkpoint, ["fork"] = createFork.IsChecked == true, ["text"] = input.Text ?? "", ["attachments"] = JsonSerializer.SerializeToNode(files.ToArray(), StoreJsonContext.Default.AttachmentArray) });
                if (response?["id"]?.GetValue<string>() is not { } id) throw new IOException("The history change did not complete. Check the connection and try again.");
                popup.Hide(); selected(id);
            }
            catch (Exception failure) { error.Text = failure.Message; }
            finally { apply.IsEnabled = true; input.IsEnabled = true; createFork.IsEnabled = message is not null; }
        };
        popup.Content = new StackPanel { Width = Math.Min(420, Math.Max(240, (TopLevel.GetTopLevel(anchor)?.ClientSize.Width ?? 480) - 60)), Spacing = 8,
            Children = { new TextBlock { Text = edit ? "Edit earlier message" : "Continue from this point", FontSize = 16 },
                new TextBlock { Text = "Replaces later conversation unless you choose a fork. Changes already made to project files are kept.", MaxWidth = 400, TextWrapping = TextWrapping.Wrap }, input, attachments, createFork, error, apply } };
        popup.ShowAt(anchor); input.Focus();
    }
}
