using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;

namespace CodexManager;

public sealed class RemoteWorkspacePicker : UserControl
{
    public event Action? Closed;
    public string? OpenedWorkspaceId { get; private set; }
    private readonly Func<JsonObject, Task<JsonNode?>> call;
    private readonly ComboBox location = new() { Name = "RemoteLocation", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox path = new() { Name = "RemoteFolderPath", PlaceholderText = "Folder path on the host" };
    private readonly TextBox filter = new() { PlaceholderText = "Filter folders…" };
    private readonly ListBox folders = new() { Name = "RemoteFolders" };
    private readonly TextBlock error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string[] entries = [];
    private string current = "", parent = "";
    private int generation;
    private readonly NewFolderButton newFolder;
    private sealed record Location(string? Distro, string Caption) { public override string ToString() => Caption; }
    private string? Distro => (location.SelectedItem as Location)?.Distro;
    public RemoteWorkspacePicker(Func<JsonObject, Task<JsonNode?>> call, string computerName = "remote computer")
    {
        this.call = call;
        newFolder = new NewFolderButton(async folderName =>
        {
            var revision = generation;
            var result = await call(new() { ["method"] = "directory/create", ["path"] = current, ["name"] = folderName, ["distro"] = Distro });
            if (result is null) throw new IOException("Could not create the folder. Check the connection and update the host if needed.");
            if (revision == generation) { filter.Text = ""; await Navigate(result["path"]!.GetValue<string>()); }
        }) { IsEnabled = false };
        ScrollViewer.SetHorizontalScrollBarVisibility(folders, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        if (OperatingSystem.IsAndroid()) ScrollViewer.SetVerticalScrollBarVisibility(folders, Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden);
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 8 };
        void Row(Control c, int r) { Grid.SetRow(c, r); grid.Children.Add(c); }
        Row(new TextBlock { Text = "Open folder on " + computerName, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 22 }, 0); Row(location, 1); Row(path, 2);
        var up = new IconButton { Icon = "chevron-up", Label = "Parent folder" }; up.Click += async (_, _) => await Navigate(parent);
        var home = new Button { Content = "Home" }; home.Click += async (_, _) => await Navigate("");
        var go = new Button { Content = "Go" }; go.Click += async (_, _) => await Navigate(path.Text ?? "");
        Row(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4, Children = { up, home, go, newFolder } }, 3);
        Row(filter, 4); Row(folders, 5); Row(error, 6);
        folders.ItemTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock { Text = "▸  " + value?.TrimEnd('/', '\\').Split('/', '\\').Last(), Margin = new Thickness(4, 10) });
        folders.SelectionChanged += (_, _) => { if (folders.SelectedItem is string selected) path.Text = selected; };
        folders.DoubleTapped += async (_, _) => { if (folders.SelectedItem is string selected) await Navigate(selected); };
        path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Navigate(path.Text ?? ""); } };
        filter.TextChanged += (_, _) => Filter();
        location.SelectionChanged += async (_, _) => { current = ""; newFolder.Flyout?.Hide(); await Navigate(""); };
        var open = new Button { Name = "OpenRemoteFolder", Content = "Open folder", Classes = { "accent" } };
        open.Click += async (_, _) =>
        {
            open.IsEnabled = false;
            try
            {
                var destination = path.Text?.Trim() ?? current;
                var name = destination.TrimEnd('/', '\\').Split('/', '\\').Last();
                if (await call(new() { ["method"] = "workspace", ["path"] = destination, ["name"] = name.Length == 0 ? destination : name, ["distro"] = Distro }) is { } opened) { OpenedWorkspaceId = opened.GetValue<string>(); Closed?.Invoke(); }
                else error.Text = "Could not open this folder. Check that it exists on the host.";
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { open.IsEnabled = true; }
        };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Closed?.Invoke();
        Row(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, open } }, 7);
        Content = grid;
        AttachedToVisualTree += async (_, _) =>
        {
            var revision = generation;
            try
            {
                var locations = await call(new() { ["method"] = "locations" });
                if (revision != generation) return;
                location.ItemsSource = new[] { new Location(null, "Files on " + computerName) }.Concat(locations?["distros"]?.AsArray().Select(d => new Location(d!.GetValue<string>(), d.GetValue<string>() + " (WSL)")) ?? []).ToArray();
                location.SelectedIndex = 0;
            }
            catch (Exception ex) { if (revision == generation) error.Text = "Could not load locations: " + ex.Message; }
        };
        DetachedFromVisualTree += (_, _) => ++generation;
    }
    private async Task Navigate(string destination)
    {
        var request = ++generation; newFolder.IsEnabled = false; error.Text = "Loading folders…";
        try
        {
            var result = await call(new() { ["method"] = "directories", ["path"] = destination, ["distro"] = Distro });
            if (request != generation) return;
            if (result is null) { error.Text = "Could not list folders. Update the host app if remote browsing is unavailable."; return; }
            current = path.Text = result["path"]!.GetValue<string>(); parent = result["parent"]!.GetValue<string>();
            entries = result["directories"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray(); Filter();
            error.Text = "Select a folder to open it. Double-tap to browse inside.";
        }
        catch (Exception ex) { if (request == generation) error.Text = ex.Message; }
        finally { if (request == generation) newFolder.IsEnabled = current.Length > 0; }
    }
    private void Filter() => folders.ItemsSource = entries.Where(e => e.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
}
