using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

// The host's terminal tabs, per workspace. A remote client opens these same shells rather than
// shells of its own, so a terminal looks the same from the phone and from the host's window.
public static class HostTerminals
{
    // A remote client opened or closed one; the host's window updates its tabs to match.
    public static event Action<Store, string>? Changed;
    private static string Key(string workspaceId) => "terminalTabs:" + workspaceId;
    public static string[] Ids(Store store, string workspaceId) => (store.Setting(Key(workspaceId)) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    // Merge rather than overwrite: the window and remote clients both add and remove tabs.
    public static void Add(Store store, string workspaceId, string id) => store.Setting(Key(workspaceId), string.Join("\n", Ids(store, workspaceId).Append(id).Distinct()));
    public static void Remove(Store store, string workspaceId, string id) => store.Setting(Key(workspaceId), string.Join("\n", Ids(store, workspaceId).Where(i => i != id)));
    internal static void Announce(Store store, string workspaceId) => Changed?.Invoke(store, workspaceId);
}

// Access goes through the same authenticated host connection as chat and files.
public sealed class RemoteTerminals(Store store, IList<Workspace> workspaces) : IDisposable
{
    private sealed class Shell : IDisposable
    {
        public readonly TerminalSession Session = new();
        public readonly StringBuilder Buffer = new();
        public long Start;
        public string WorkspaceId = "";
        public bool Exited;
        public Shell()
        {
            Session.Completed += () => Exited = true; Session.RawOutput += text =>
        {
            Buffer.Append(text);
            if (Buffer.Length > 262144) { var trim = Buffer.Length - 131072; Buffer.Remove(0, trim); Start += trim; }
        };
        }
        public void Dispose() => Session.Dispose();
    }
    private const string Closed = "This terminal has closed. Open it again to start a new shell.";
    private readonly Dictionary<string, Shell> shells = [];
    private bool disposed;
    private Workspace Find(string id) => workspaces.FirstOrDefault(w => w.Id == id) ?? throw new IOException("Select a workspace on the host first.");
    // Attach to a host terminal that is still running, or start a new one when `create` is set.
    private async Task<string> Attach(Workspace workspace, string id, bool create)
    {
        foreach (var stale in shells.Where(s => s.Value.Exited).Select(s => s.Key).ToArray()) { shells[stale].Dispose(); shells.Remove(stale); }
        if (shells.ContainsKey(id)) return id;
        if (shells.Count >= 16) throw new IOException("Close an existing remote terminal before opening another.");
        var shell = new Shell { WorkspaceId = workspace.Id }; shells.Add(id, shell);
        try { await shell.Session.Start(workspace, settings: store, durableId: id, resumeOnly: !create); }
        catch (Exception error)
        {
            shells.Remove(id); shell.Dispose();
            if (!create && error.Message.StartsWith("This terminal has closed", StringComparison.Ordinal))
            {
                if (HostTerminals.Ids(store, workspace.Id).Contains(id)) { HostTerminals.Remove(store, workspace.Id, id); HostTerminals.Announce(store, workspace.Id); }
                throw new IOException(Closed);
            }
            throw;
        }
        return id;
    }
    public async Task<JsonNode?> Handle(JsonObject request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string Text(string key) => request[key]?.GetValue<string>() ?? "";
        var method = Text("method");
        if (method == "terminal/list")
            return new JsonObject { ["terminals"] = new JsonArray(HostTerminals.Ids(store, Find(Text("workspaceId")).Id).Select(id => (JsonNode)id).ToArray()) };
        if (method == "terminal/open")
        {
            var workspace = Find(Text("workspaceId"));
            if (request["terminalId"]?.GetValue<string>() is { Length: > 0 } wanted)
            {
                if (!HostTerminals.Ids(store, workspace.Id).Contains(wanted)) throw new IOException(Closed);
                return new JsonObject { ["id"] = await Attach(workspace, wanted, create: false) };
            }
            // Clients that do not choose a terminal get the host's first one that is still running.
            if (request["new"]?.GetValue<bool>() != true)
            {
                // A shell from before remote terminals were shared joins the host's tabs.
                var legacy = "remote:" + workspace.Id;
                if (!HostTerminals.Ids(store, workspace.Id).Contains(legacy))
                    try { await Attach(workspace, legacy, create: false); HostTerminals.Add(store, workspace.Id, legacy); HostTerminals.Announce(store, workspace.Id); }
                    catch (IOException) { }
                foreach (var id in HostTerminals.Ids(store, workspace.Id))
                    try { return new JsonObject { ["id"] = await Attach(workspace, id, create: false) }; }
                    catch (IOException error) when (error.Message == Closed) { }
            }
            var created = "desktop:" + Guid.NewGuid().ToString("N");
            HostTerminals.Add(store, workspace.Id, created);
            try { await Attach(workspace, created, create: true); }
            catch { HostTerminals.Remove(store, workspace.Id, created); throw; }
            HostTerminals.Announce(store, workspace.Id);
            return new JsonObject { ["id"] = created };
        }
        var requestedId = Text("terminalId");
        // A client may reconnect after the host UI restarted. Reattach the host terminal to the
        // surviving helper before reading it.
        if (!shells.ContainsKey(requestedId) && workspaces.FirstOrDefault(w => HostTerminals.Ids(store, w.Id).Contains(requestedId)) is { } owner)
            await Attach(owner, requestedId, create: false);
        if (!shells.TryGetValue(requestedId, out var active)) throw new IOException(Closed);
        switch (method)
        {
            case "terminal/read":
                var offset = request["offset"]?.GetValue<long>() ?? 0;
                var end = active.Start + active.Buffer.Length;
                var start = Math.Clamp(offset, active.Start, end);
                return new JsonObject { ["text"] = active.Buffer.ToString((int)(start - active.Start), (int)(end - start)), ["offset"] = end, ["reset"] = offset < active.Start || offset > end, ["exited"] = active.Exited };
            case "terminal/input":
                var input = Text("text");
                if (input.Length > 65536) throw new IOException("Terminal input is too large.");
                active.Session.Input(input); break;
            case "terminal/resize":
                active.Session.Resize(request["cols"]!.GetValue<int>(), request["rows"]!.GetValue<int>()); break;
            case "terminal/close":
                await active.Session.Close(); active.Dispose(); shells.Remove(requestedId);
                HostTerminals.Remove(store, active.WorkspaceId, requestedId); HostTerminals.Announce(store, active.WorkspaceId); break;
            default: throw new IOException("Unknown terminal operation.");
        }
        return JsonValue.Create(true);
    }
    public void Dispose() { disposed = true; foreach (var shell in shells.Values) shell.Dispose(); shells.Clear(); }
}
