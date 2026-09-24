using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace CodexManager;

public sealed class WorkspaceDialog : Window
{
    public const string LocalOption = "This computer";
    public const string RemoteOption = "Remote computer…";
    public RemoteHost? PairedHost { get; private set; }
    private readonly ComboBox host = new() { Name = "HostPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox path = new() { Name = "FolderPath", PlaceholderText = "Absolute workspace path" };
    private readonly TextBox name = new() { PlaceholderText = "Workspace name (optional)" };
    private readonly TextBox filter = new() { Name = "FolderFilter", PlaceholderText = "Filter folders…" };
    private readonly CheckBox hidden = new() { Content = "Hidden folders", VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox folders = new() { Name = "FolderList", MinHeight = 170 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Stack<string> back = [];
    private string[] directories = [];
    private string? displayedPath;
    private int navigation;
    private readonly Workspace? initial;
    private readonly NewFolderButton newFolder;

    public WorkspaceDialog(Workspace? initialWorkspace = null, Store? connectionStore = null)
    {
        initial = initialWorkspace;
        newFolder = new NewFolderButton(async folderName =>
        {
            var revision = navigation;
            var destination = await Hosts.CreateDirectory(Distro, displayedPath ?? "", folderName);
            if (revision == navigation) await Navigate(destination);
        }) { IsEnabled = false };
        Title = "Open workspace"; Width = 640; Height = 680; MinWidth = 500; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        folders.ItemTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock { Text = "▸  " + value?.TrimEnd('/', '\\').Split('/', '\\').Last(), Margin = new Thickness(4, 5) });
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await Browse();
        var up = new Button { Name = "ParentFolderButton", Content = "↑ Parent" }; up.Click += async (_, _) => await NavigateParent();
        var home = new Button { Name = "HomeFolderButton", Content = "⌂ Home" }; home.Click += async (_, _) => await Home();
        var previous = new Button { Content = "← Back" }; previous.Click += async (_, _) => { if (back.TryPop(out var p)) await Navigate(p, false); };
        var open = new Button { Name = "OpenFolderButton", Content = "Open current folder", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right };
        folders.SelectionChanged += (_, _) =>
        {
            if (folders.SelectedItem is string selected) path.Text = selected;
            open.Content = folders.SelectedItem is string ? "Open selected folder" : "Open current folder";
        };
        path.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && folders.SelectedItem is string selected && path.Text != selected) folders.SelectedItem = null; };
        open.Click += async (_, _) =>
        {
            try
            {
                var p = path.Text?.Trim() ?? "";
                var w = new Workspace(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name.Text) ? Workspace.DefaultName(p) : name.Text.Trim(), p, Distro);
                open.IsEnabled = false; await Hosts.Validate(w); Close(w);
            }
            catch (Exception ex) { error.Text = "Could not open folder: " + ex.Message; }
            finally { open.IsEnabled = true; }
        };
        folders.DoubleTapped += async (_, _) => await EnterFolder();
        folders.KeyDown += async (_, e) => { if (e.Key is Key.Enter or Key.Right) { e.Handled = true; await EnterFolder(); } else if (e.Key is Key.Back or Key.Left) { e.Handled = true; await NavigateParent(); } };
        path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Navigate(path.Text ?? ""); } };
        filter.TextChanged += (_, _) => Filter(); hidden.IsCheckedChanged += (_, _) => Filter();
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 8 };
        void Row(Control control, int index) { Grid.SetRow(control, index); grid.Children.Add(control); }
        Row(new TextBlock { Text = "Choose where Codex works", FontSize = 23 }, 0); Row(host, 1); Row(path, 2);
        Row(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4, Children = { previous, up, home, browse, newFolder } }, 3);
        var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 }; filterRow.Children.Add(filter); Grid.SetColumn(hidden, 1); filterRow.Children.Add(hidden); Row(filterRow, 4);
        Row(folders, 5); Row(name, 6); Row(error, 7); Row(open, 8); Content = grid;
        var localControls = grid.Children.Where(c => Grid.GetRow(c) >= 2).ToArray();
        ConnectionSettingsView? settings = null;
        Store? ownedStore = null;
        host.SelectionChanged += async (_, _) =>
        {
            ++navigation; back.Clear(); displayedPath = null; newFolder.IsEnabled = false; newFolder.Flyout?.Hide();
            var remote = Equals(host.SelectedItem, RemoteOption);
            foreach (var control in localControls) control.IsVisible = !remote;
            if (settings is not null) { grid.Children.Remove(settings); settings.Dispose(); settings = null; }
            if (remote)
            {
                settings = new ConnectionSettingsView(connectionStore ?? (ownedStore ??= new Store()), true, () => Task.CompletedTask);
                settings.Paired += paired => { PairedHost = paired; Close(); };
                Grid.SetRow(settings, 2); Grid.SetRowSpan(settings, 7); grid.Children.Add(settings);
                return;
            }
            if (initial is not null && Distro == initial.Distro) await Navigate(initial.Path); else await Home();
        };
        Closed += (_, _) => { ++navigation; settings?.Dispose(); ownedStore?.Dispose(); };
        Opened += async (_, _) =>
        {
            host.IsEnabled = false; error.Text = "Discovering WSL distributions…";
            try { host.ItemsSource = new[] { LocalOption }.Concat(await Hosts.Distros()).Append(RemoteOption).ToArray(); }
            catch (Exception ex) { host.ItemsSource = new[] { LocalOption, RemoteOption }; error.Text = ex.Message; }
            finally { host.IsEnabled = true; host.SelectedItem = initial?.Distro ?? LocalOption; if (host.SelectedIndex < 0) host.SelectedIndex = 0; }
        };
    }
    private string? Distro => host.SelectedItem is string selected && selected != LocalOption && selected != RemoteOption ? selected : null;
    private async Task Home()
    {
        try
        {
            var p = Distro is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : (await Hosts.Capture(Hosts.Info("wsl.exe", "-d", Distro, "--exec", "sh", "-c", "printf '%s' \"$HOME\""))).Trim();
            await Navigate(p);
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }
    private Task EnterFolder() => folders.SelectedItem is string selected ? Navigate(selected) : Task.CompletedTask;
    private Task NavigateParent()
    {
        var p = displayedPath ?? path.Text ?? "/";
        var parent = Distro is null ? Directory.GetParent(p)?.FullName ?? p : p.TrimEnd('/').LastIndexOf('/') is > 0 and var i ? p[..i] : "/";
        return Navigate(parent);
    }
    private async Task Browse()
    {
        if (Distro is null)
        {
            var selection = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose workspace", AllowMultiple = false });
            if (selection.Count > 0) await Navigate(selection[0].TryGetLocalPath() ?? "");
        }
        else await Navigate(path.Text ?? "/");
    }
    private async Task Navigate(string destination, bool remember = true)
    {
        var generation = ++navigation; var distro = Distro; folders.IsEnabled = false; newFolder.IsEnabled = false; error.Text = "Loading folders…";
        try
        {
            var result = await Hosts.Directories(distro, destination);
            if (generation != navigation) return;
            if (remember && displayedPath is not null && displayedPath != destination) back.Push(displayedPath);
            folders.SelectedItem = null;
            displayedPath = destination; path.Text = destination; directories = result; filter.Text = ""; Filter(); error.Text = result.Length == 0 ? "This folder has no subfolders. You can open it as a workspace." : "Select a folder to open it. Double-click to browse inside.";
        }
        catch (Exception ex) { if (generation == navigation) error.Text = ex.Message; }
        finally { if (generation == navigation) { folders.IsEnabled = true; newFolder.IsEnabled = displayedPath is not null; } }
    }
    private void Filter()
    {
        var selected = folders.SelectedItem as string;
        folders.ItemsSource = directories.Where(p => (hidden.IsChecked == true || !p.TrimEnd('/', '\\').Split('/', '\\').Last().StartsWith('.')) && p.TrimEnd('/', '\\').Split('/', '\\').Last().Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected is not null && folders.Items.Contains(selected)) folders.SelectedItem = selected;
        else if (selected is not null && path.Text == selected) path.Text = displayedPath;
    }
}
