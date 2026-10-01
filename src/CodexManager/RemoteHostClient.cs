using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace CodexManager;

// One connection to a paired computer for the desktop: it keeps the host's catalog (workspaces
// and chat summaries) current for the sidebar, raises notifications for its pending requests,
// and carries every RPC that the host's chats (RemoteChatSession) make. All members run on the
// UI thread.
public sealed class RemoteHostClient : IDisposable
{
    public RemoteHost Host { get; }
    public string Scope => "remote:" + Host.Address + ":" + Host.Port + ":";
    public string Scoped(string id) => Scope + id;
    public JsonNode? Catalog { get; private set; }
    public string Status { get; private set; } = "Connecting…";
    public bool Connected => connection is not null;
    public IReadOnlyList<AgentProvider> Providers { get; private set; } = [];
    public event Action? CatalogChanged;
    public event Action? StatusChanged;
    private readonly Func<bool> allowAll;
    private readonly Action<string> openChat;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> notified = [];
    private RemoteConnection? connection;
    private CancellationTokenSource? connectAttempt;
    private DateTimeOffset reconnectAfter;
    private int failures;
    private bool connecting, refreshing, collapsed, suspended, hostOffline;
    private string catalogJson = "";

    public RemoteHostClient(RemoteHost host, Func<bool> allowAll, Action<string> openChat)
    {
        Host = host; this.allowAll = allowAll; this.openChat = openChat;
        timer.Tick += async (_, _) =>
        {
            if (collapsed || suspended || lifetime.IsCancellationRequested) return;
            if (connection is null) { if (!connecting && DateTimeOffset.UtcNow >= reconnectAfter) await Connect(); return; }
            await RefreshCatalog();
        };
    }
    public void Start() { if (!collapsed && !suspended) { timer.Start(); _ = Connect(); } }
    public bool Collapsed
    {
        get => collapsed;
        set
        {
            if (collapsed == value) return;
            collapsed = value;
            if (value) { Disconnect("Connection collapsed — expand it in the sidebar to resume."); DismissNotifications(); }
            else { reconnectAfter = default; Start(); }
        }
    }
    // Paused while the app is suspended (mobile background) or its UI sleeps.
    public void SetSuspended(bool value)
    {
        if (suspended == value) return;
        suspended = value;
        if (value) { timer.Stop(); connectAttempt?.Cancel(); }
        else { reconnectAfter = default; Start(); }
    }
    public void Reconnect()
    {
        if (lifetime.IsCancellationRequested) return;
        Disconnect("Reconnecting…"); reconnectAfter = default; failures = 0; Start();
    }
    private void Disconnect(string status)
    {
        connectAttempt?.Cancel(); connection?.Dispose(); connection = null;
        SetStatus(status);
    }
    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status; StatusChanged?.Invoke();
    }
    private async Task Connect()
    {
        if (connecting || collapsed || suspended || lifetime.IsCancellationRequested) return;
        connecting = true;
        connection?.Dispose(); connection = null;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connectAttempt = attempt; attempt.CancelAfter(TimeSpan.FromSeconds(30));
        SetStatus(hostOffline ? "Offline · checking connection…" : "Connecting…");
        RemoteConnection? candidate = null;
        try
        {
            candidate = new RemoteConnection(Host);
            await candidate.Connect(attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            connection = candidate; hostOffline = false; SetStatus("Connected");
            await RefreshCatalog();
            if (connection is not null) failures = 0;
        }
        catch (Exception error)
        {
            if (!collapsed && !suspended && !lifetime.IsCancellationRequested && !attempt.IsCancellationRequested)
            {
                hostOffline = candidate?.TransportConnected == false;
                SetStatus(hostOffline ? $"Offline · cannot reach {Host.Address}:{Host.Port}. Retrying automatically."
                    : error.Message + (candidate?.ObservedFingerprint is { } pin && pin != Host.Fingerprint
                        ? "\nObserved host fingerprint: " + pin + "\nVerify it on the host before changing the saved fingerprint." : "\nRetrying connection…"));
            }
            candidate?.Dispose(); if (ReferenceEquals(connection, candidate)) connection = null;
        }
        finally
        {
            connectAttempt = null; connecting = false;
            if (connection is null) reconnectAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))));
        }
    }
    // Returns null when the host is unreachable or refused; the reason is in Status.
    public async Task<JsonNode?> Call(JsonObject request)
    {
        if (collapsed || suspended || lifetime.IsCancellationRequested) return null;
        var client = connection;
        if (client is null) { if (!hostOffline && !connecting) SetStatus("Reconnecting to the host…"); return null; }
        var method = request["method"]?.GetValue<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(method is "list" or "chat" or "terminal/read" or "file/read" ? 15 : method == "delete" ? 120 : 90));
        try
        {
            var result = await client.Request(request, timeout.Token);
            return !lifetime.IsCancellationRequested && ReferenceEquals(connection, client) ? result : null;
        }
        catch (RemoteOperationException error)
        {
            LastError = error.Message;
            return method == "terminal/read" ? new JsonObject { ["error"] = error.Message } : null;
        }
        catch (RemoteRequestBusyException) { return null; }
        catch (Exception error)
        {
            client.Dispose();
            if (ReferenceEquals(connection, client))
            {
                connection = null; reconnectAfter = DateTimeOffset.UtcNow.AddSeconds(1);
                if (!collapsed && !suspended && !lifetime.IsCancellationRequested) SetStatus("Connection interrupted: " + error.Message + " Retrying…");
            }
            return null;
        }
    }
    // The last error the host reported for a request (for example "Enter a message.").
    public string? LastError { get; private set; }
    public async Task RefreshCatalog()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var result = await Call(new() { ["method"] = "list" });
            if (result is null || lifetime.IsCancellationRequested) return;
            Providers = AgentProviders.All.Select(p => p.Provider).Where(p => result["providers"] is JsonArray providers
                ? providers.Any(v => v?.GetValue<string>() == p.ToString()) : !AgentProviders.IsAdditional(p)).ToArray();
            await NotifyPending(result);
            var json = result.ToJsonString();
            if (json == catalogJson) return;
            catalogJson = json; Catalog = result; CatalogChanged?.Invoke();
        }
        catch (Exception error) { AppDiagnostics.Record("Remote catalog refresh", error); }
        finally { refreshing = false; }
    }
    private async Task NotifyPending(JsonNode result)
    {
        if (result["permissions"] is not JsonArray pending) return;
        var active = new HashSet<string>();
        foreach (var permission in pending.OfType<JsonObject>())
        {
            var id = permission["id"]!.GetValue<string>();
            using var parsed = System.Text.Json.JsonDocument.Parse(permission.ToJsonString());
            if (allowAll() && PermissionPolicy.AllowedOption(parsed.RootElement) is { } allowed
                && await Call(new() { ["method"] = "approve", ["permissionId"] = id, ["optionId"] = allowed }) is not null) continue;
            active.Add(id);
            var chat = permission["chatId"]!.GetValue<string>();
            if (notified.Add(id)) PermissionNotifications.Show(Host.Address + id, permission["chatTitle"]?.GetValue<string>() ?? "Remote chat", () => openChat(chat));
        }
        if (result["elicitations"] is JsonArray questions)
            foreach (var question in questions.OfType<JsonObject>())
            {
                var id = question["id"]!.GetValue<string>(); active.Add(id);
                var chat = question["chatId"]!.GetValue<string>();
                if (notified.Add(id)) PermissionNotifications.Show(Host.Address + id, question["chatTitle"]?.GetValue<string>() ?? "Remote chat", () => openChat(chat), "Question from agent");
            }
        foreach (var resolved in notified.Except(active).ToArray()) { PermissionNotifications.Dismiss(Host.Address + resolved); notified.Remove(resolved); }
    }
    private void DismissNotifications()
    {
        foreach (var id in notified) PermissionNotifications.Dismiss(Host.Address + id);
        notified.Clear();
    }
    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        timer.Stop(); lifetime.Cancel(); connectAttempt?.Cancel();
        var client = connection; connection = null; client?.Dispose();
        DismissNotifications(); CatalogChanged = null; StatusChanged = null;
    }
}
