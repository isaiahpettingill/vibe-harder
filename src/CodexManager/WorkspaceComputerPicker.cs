using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CodexManager;

// Connections always originate on this client. Remote catalogs contain only that host's own workspaces.
public sealed class WorkspaceComputerPicker : Grid, IDisposable
{
    public sealed record Computer(RemoteHost? Host)
    {
        public override string ToString() => Host is null ? "This computer" : "Remote · " + Host.Name + " (" + Host.Address + ")";
    }
    public event Action<Workspace>? LocalChosen;
    public event Action? BrowseLocal;
    public event Action? PairComputer;
    public event Action<RemoteHost, string>? RemoteChosen;
    private readonly WorkspaceHistory history;
    private readonly ComboBox computer = new() { Name = "WorkspaceComputer", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl body = new();
    private readonly CancellationTokenSource lifetime = new();
    private RemoteConnection? connection;
    private CancellationTokenSource? selection;
    private int generation;
    private bool disposed;

    public WorkspaceComputerPicker(Store store, bool remoteOnly, RemoteHost? selected = null)
    {
        history = new WorkspaceHistory(store);
        RowDefinitions = new("Auto,*,Auto"); RowSpacing = 8; Width = 400; MaxHeight = 600;
        Children.Add(computer); SetRow(body, 1); Children.Add(body);
        var pair = new Button { Content = "＋ Pair another computer", HorizontalAlignment = HorizontalAlignment.Stretch };
        pair.Click += (_, _) => PairComputer?.Invoke(); SetRow(pair, 2); Children.Add(pair);
        computer.ItemsSource = (remoteOnly ? Array.Empty<Computer>() : [new Computer(null)]).Concat(RemoteSettings.Hosts(store).Select(h => new Computer(h))).ToArray();
        computer.SelectionChanged += async (_, _) => await ShowComputer();
        computer.SelectedItem = computer.Items.OfType<Computer>().FirstOrDefault(c => selected is not null && c.Host == selected) ?? computer.Items.OfType<Computer>().FirstOrDefault();
    }

    private async Task ShowComputer()
    {
        var revision = ++generation;
        selection?.Cancel(); selection?.Dispose(); selection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connection?.Dispose(); connection = null;
        if (computer.SelectedItem is not Computer chosen) return;
        if (chosen.Host is null)
        {
            var selector = new WorkspaceSelector(history.Entries()); body.Content = selector;
            selector.Chosen += workspace => LocalChosen?.Invoke(workspace);
            selector.Removed += workspace => { history.Remove(workspace); selector.Refresh(history.Entries()); };
            selector.Browse += () => BrowseLocal?.Invoke();
            return;
        }
        var host = chosen.Host;
        var status = new TextBlock { Text = "Connecting to " + host.Name + "…", TextWrapping = Avalonia.Media.TextWrapping.Wrap }; body.Content = status;
        var candidate = new RemoteConnection(host); connection = candidate;
        try
        {
            await candidate.Connect(selection.Token);
            if (revision != generation) return;
            var catalog = await Call(new() { ["method"] = "list" });
            if (revision != generation || catalog is null) return;
            var workspaces = catalog["workspaces"]!.AsArray().Select(w => new Workspace(w!["id"]!.GetValue<string>(), w["name"]!.GetValue<string>(), w["path"]?.GetValue<string>() ?? "", w["distro"]?.GetValue<string>())).ToArray();
            void ShowHistory()
            {
                var selector = new WorkspaceSelector(workspaces, allowRemoval: false, remotePlatform: catalog["platform"]?.GetValue<string>() ?? "linux");
                body.Content = selector;
                selector.Chosen += workspace => RemoteChosen?.Invoke(host, workspace.Id);
                selector.Browse += () =>
                {
                    var browser = new RemoteWorkspacePicker(Call, host.Name) { Height = Math.Min(480, Math.Max(200, MaxHeight - 100)) };
                    body.Content = browser;
                    browser.Closed += () => { if (browser.OpenedWorkspaceId is { } id) RemoteChosen?.Invoke(host, id); else ShowHistory(); };
                };
            }
            ShowHistory();
        }
        catch (Exception error)
        {
            if (revision != generation || lifetime.IsCancellationRequested) return;
            status.Text = "Could not connect to " + host.Name + ": " + error.Message;
            var retry = new Button { Content = "Retry" }; retry.Click += async (_, _) => await ShowComputer();
            body.Content = new StackPanel { Spacing = 8, Children = { status, retry } };
        }
    }

    private async Task<JsonNode?> Call(JsonObject request)
    {
        var client = connection ?? throw new IOException("Select a computer first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(selection?.Token ?? lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await client.Request(request, timeout.Token);
    }

    public void Dispose() { if (disposed) return; disposed = true; ++generation; lifetime.Cancel(); selection?.Cancel(); connection?.Dispose(); selection?.Dispose(); lifetime.Dispose(); }
}
