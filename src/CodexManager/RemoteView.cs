using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;

namespace CodexManager;

public sealed class RemoteView : UserControl, IDisposable
{
    private readonly RemoteHost host;
    public RemoteHost Host => host;
    private RemoteConnection? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox composer = new() { AcceptsReturn = true, MinHeight = 80, PlaceholderText = "Message the agent…" };
    private readonly StackPanel approvals = new();
    private readonly ListBox chats = new();
    private readonly ComboBox workspaces = new();
    private readonly ObservableCollection<Message> messages = [];
    private string? chatId;
    private bool polling;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string permissionsJson = "";
    private string configJson = "";
    private readonly StackPanel configs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly List<Attachment> attachments = [];
    private JsonArray chatRows = [];
    private int polls;
    private bool presentationSleeping;
    public void SetPresentationSleeping(bool sleeping)
    { presentationSleeping = sleeping; if (sleeping) { timer.Stop(); messages.Clear(); } else timer.Start(); }
    private bool refreshing;
    private bool viewingHistory;
    public event Action<JsonNode>? CatalogChanged;
    public void SelectChat(string id)
    {
        viewingHistory = false; chatId = id; if (connection is not null) _ = Call(new() { ["method"] = "read", ["chatId"] = id }); messages.Clear(); permissionsJson = ""; configJson = "";
        var owner = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == id)?["workspaceId"]?.GetValue<string>();
        if (owner is not null) workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == owner);
    }
    public RemoteView(RemoteHost host)
    {
        this.host = host;
        var panel = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        var top = new StackPanel { Spacing = 6 }; top.Children.Add(new TextBlock { Text = host.Name + " · " + host.Address }); top.Children.Add(status);
        var connect = new Button { Content = "Connect / reconnect" }; connect.Click += async (_, _) => await Connect(); top.Children.Add(connect);
        var select = new Grid { ColumnDefinitions = new("*,Auto") }; workspaces.HorizontalAlignment = HorizontalAlignment.Stretch; select.Children.Add(workspaces);
        workspaces.SelectionChanged += (_, _) => FilterChats();
        var folder = new Button { Content = "Open folder on host…" }; top.Children.Add(folder);
        var import = new Button { Content = "Import host history ▾" }; top.Children.Add(import);
        import.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            foreach (var provider in AgentProviders.All)
            {
                var item = new MenuItem { Header = provider.Name };
                item.Click += async (_, _) => { if (workspaces.SelectedItem is RemoteItem workspace) { await Call(new() { ["method"] = "import", ["workspaceId"] = workspace.Id, ["provider"] = provider.Provider.ToString() }); await RefreshList(); } };
                menu.Items.Add(item);
            }
            menu.ShowAt(import);
        };
        folder.Click += async (_, _) =>
        {
            var dialog = new Window { Title = "Remote workspace", Width = 440, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var path = new TextBox { PlaceholderText = "Folder path on remote host" }; var name = new TextBox { PlaceholderText = "Workspace name" }; var distro = new TextBox { PlaceholderText = "WSL distro on remote Windows host (optional)" }; var open = new Button { Content = "Open" };
            open.Click += async (_, _) => { if (await Call(new() { ["method"] = "workspace", ["path"] = path.Text, ["name"] = name.Text, ["distro"] = distro.Text }) is not null) { dialog.Close(); await RefreshList(); } };
            dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { name, path, distro, open } }; await dialog.ShowDialog((Window)TopLevel.GetTopLevel(this)!);
        };
        var create = new Button { Content = "New chat ▾" }; Grid.SetColumn(create, 1); select.Children.Add(create); top.Children.Add(select);
        create.Click += (_, _) => { var menu = new MenuFlyout(); foreach (var provider in AgentProviders.All) { var item = new MenuItem { Header = provider.Name }; item.Click += async (_, _) => { if (workspaces.SelectedItem is RemoteItem workspace) { var result = await Call(new() { ["method"] = "create", ["workspaceId"] = workspace.Id, ["provider"] = provider.Provider.ToString() }); chatId = result?["id"]?.GetValue<string>(); messages.Clear(); await RefreshList(); } }; menu.Items.Add(item); } menu.ShowAt(create); };
        panel.Children.Add(top);
        var split = new Grid { ColumnDefinitions = new("0,0,*") }; Grid.SetRow(split, 1); panel.Children.Add(split);
        chats.SelectionChanged += (_, _) => { if (chats.SelectedItem is RemoteItem selected && chatId != selected.Id) { chatId = selected.Id; messages.Clear(); permissionsJson = ""; } };
        chats.IsVisible = false; split.Children.Add(chats); var divider = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false }; Grid.SetColumn(divider, 1); split.Children.Add(divider);
        var output = new ListBox { ItemsSource = messages, ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()), Background = Avalonia.Media.Brushes.Transparent, ItemTemplate = new FuncDataTemplate<Message>((message, _) => { var view = new MessageView { Margin = new Thickness(8) }; view.DataContextChanged += (_, _) => view.Message = view.DataContext as Message; return view; }, true) };
        output.ItemContainerTheme = (Avalonia.Styling.ControlTheme)this.FindResource("TranscriptItemTheme")!;
        ScrollViewer.SetVerticalScrollBarVisibility(output, Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
        ScrollViewer.SetAllowAutoHide(output, false);
        Grid.SetColumn(output, 2); split.Children.Add(output);
        var earlier = new IconButton { Icon = "chevron-up", Label = "Earlier messages" }; var latest = new IconButton { Icon = "latest", Label = "Return to latest messages" };
        var history = new StackPanel { Orientation = Orientation.Horizontal, Children = { earlier, latest } }; top.Children.Add(history);
        earlier.Click += async (_, _) => { if (chatId is null || messages.Count == 0) return; var id = chatId; var result = await Call(new() { ["method"] = "chat", ["chatId"] = id, ["before"] = messages[0].Sequence }); if (result is not null && id == chatId && result["messages"]!.AsArray().Count > 0) { viewingHistory = true; messages.Clear(); ApplyMessages(result["messages"]!.AsArray()); output.ScrollIntoView(0); } };
        latest.Click += (_, _) => { viewingHistory = false; messages.Clear(); };
        Grid.SetRow(approvals, 2); panel.Children.Add(approvals);
        var input = new StackPanel { Spacing = 6 }; Grid.SetRow(input, 3); panel.Children.Add(input); input.Children.Add(composer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; input.Children.Add(actions);
        input.Children.Add(configs);
        var attach = new IconButton { Icon = "add", Label = "Attach images or files" };
        attach.Click += async (_, _) =>
        {
            var selected = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = true });
            foreach (var file in selected)
            {
                await using var stream = await file.OpenReadAsync(); using var data = new MemoryStream(); await stream.CopyToAsync(data);
                if (data.Length > 20 * 1024 * 1024) { status.Text = "Attachment exceeds 20 MB."; continue; }
                var extension = Path.GetExtension(file.Name).ToLowerInvariant(); var mime = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "text/plain" };
                attachments.Add(new(file.Name, mime, mime.StartsWith("image/") ? Convert.ToBase64String(data.ToArray()) : System.Text.Encoding.UTF8.GetString(data.ToArray()), "file:///" + Uri.EscapeDataString(file.Name)));
            }
            status.Text = $"{attachments.Count} attachments ready";
        }; actions.Children.Add(attach);
        foreach (var (title, method) in new[] { ("Send / queue", "send"), ("Queue", "queue"), ("Steer", "steer"), ("Stop", "stop"), ("Resume", "resume") })
        {
            var button = new Button { Content = title, MinHeight = 36 };
            button.Click += async (_, _) => { if (chatId is null) return; var text = composer.Text ?? ""; var result = await Call(new() { ["method"] = method, ["chatId"] = chatId, ["text"] = text, ["attachments"] = System.Text.Json.JsonSerializer.SerializeToNode(attachments.ToArray(), StoreJsonContext.Default.AttachmentArray) }); if (result is not null && method is not ("stop" or "resume") && result.ToJsonString() != "false" && composer.Text == text) { composer.Text = ""; attachments.Clear(); } }; actions.Children.Add(button);
        }
        Content = panel;
        timer.Tick += async (_, _) =>
        {
            if (presentationSleeping || polling || connection is null || chatId is null) return;
            polling = true; var id = chatId;
            try
            {
                var result = await Call(new() { ["method"] = "chat", ["chatId"] = id }); if (presentationSleeping || result is null || id != chatId) return;
                status.Text = result["status"]?.GetValue<string>() + " · " + result["queued"] + " queued";
                var configText = result["config"]!.ToJsonString();
                if (configText != configJson)
                {
                    configJson = configText; configs.Children.Clear();
                    foreach (var config in result["config"]!.AsArray())
                    {
                        var button = new Button { Content = config!["current"]!.GetValue<string>() + " ▾", FontSize = 11, Padding = new Thickness(4) }; ToolTip.SetTip(button, config["name"]!.GetValue<string>());
                        button.Click += (_, _) => { var menu = new MenuFlyout(); foreach (var value in config["values"]!.AsArray()) { var item = new MenuItem { Header = value!["name"]!.GetValue<string>() }; item.Click += async (_, _) => await Call(new() { ["method"] = "config", ["chatId"] = chatId, ["configId"] = config["id"]!.DeepClone(), ["value"] = value["value"]!.DeepClone() }); menu.Items.Add(item); } menu.ShowAt(button); }; configs.Children.Add(button);
                    }
                }
                var scroll = output.Scroll;
                var follow = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 80;
                if (!viewingHistory) ApplyMessages(result["messages"]!.AsArray());
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
    private void ApplyMessages(JsonArray rows)
    {
        if (presentationSleeping) return;
        foreach (var row in rows)
        {
            var id = row!["id"]!.GetValue<string>(); var message = messages.FirstOrDefault(m => m.Id == id);
            if (message is null) { message = new Message { Id = id, Role = row["role"]!.GetValue<string>(), Sequence = row["sequence"]?.GetValue<int>() ?? 0 }; messages.Add(message); }
            message.Text = row["text"]!.GetValue<string>();
        }
        while (messages.Count > Chat.HistoryPageSize) messages.RemoveAt(0);
    }
    private async Task Connect()
    {
        connection?.Dispose(); connection = null;
        RemoteConnection? candidate = null;
        try { candidate = new RemoteConnection(host); await candidate.Connect(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested(); connection = candidate; status.Text = "Connected"; await RefreshList(); }
        catch (Exception error) { status.Text = error.Message + (candidate?.ObservedFingerprint is { } pin ? "\nObserved host fingerprint: " + pin + "\nVerify it on the host before changing the saved fingerprint." : ""); candidate?.Dispose(); }
    }
    private async Task RefreshList()
    {
        var result = await Call(new() { ["method"] = "list" }); if (result is null) return;
        CatalogChanged?.Invoke(result.DeepClone());
        chatRows = result["chats"]!.AsArray();
        var selected = chatRows.FirstOrDefault(c => c?["id"]?.GetValue<string>() == chatId)?["workspaceId"]?.GetValue<string>() ?? (workspaces.SelectedItem as RemoteItem)?.Id;
        refreshing = true;
        workspaces.ItemsSource = result["workspaces"]!.AsArray().Select(w => new RemoteItem(w!["id"]!.GetValue<string>(), w["name"]!.GetValue<string>())).ToArray();
        workspaces.SelectedItem = workspaces.Items.OfType<RemoteItem>().FirstOrDefault(w => w.Id == selected) ?? workspaces.Items.OfType<RemoteItem>().FirstOrDefault();
        refreshing = false;
        FilterChats();
    }
    private void FilterChats()
    {
        if (refreshing) return;
        var owner = (workspaces.SelectedItem as RemoteItem)?.Id;
        chats.ItemsSource = chatRows.Where(c => c!["workspaceId"]!.GetValue<string>() == owner && c["archived"]?.GetValue<bool>() != true).Select(c => new RemoteItem(c!["id"]!.GetValue<string>(), c["title"]!.GetValue<string>())).ToArray();
        chats.SelectedItem = chats.Items.OfType<RemoteItem>().FirstOrDefault(c => c.Id == chatId) ?? chats.Items.OfType<RemoteItem>().FirstOrDefault();
        if (chats.SelectedItem is null) { chatId = null; messages.Clear(); configs.Children.Clear(); approvals.Children.Clear(); }
    }
    private async Task<JsonNode?> Call(JsonObject request)
    {
        var client = connection;
        if (client is null) { status.Text = "Connect to the host first."; return null; }
        try { return await client.Request(request, lifetime.Token); }
        catch (RemoteOperationException error) { status.Text = error.Message; return null; }
        catch (Exception error) { status.Text = "Disconnected: " + error.Message + " Agents remain on the host. Reconnect to continue."; client.Dispose(); if (ReferenceEquals(connection, client)) connection = null; return null; }
    }
    public void Dispose() { timer.Stop(); lifetime.Cancel(); connection?.Dispose(); connection = null; CatalogChanged = null; messages.Clear(); chatRows.Clear(); configs.Children.Clear(); approvals.Children.Clear(); Content = null; }
    private sealed record RemoteItem(string Id, string Name) { public override string ToString() => Name; }
}
