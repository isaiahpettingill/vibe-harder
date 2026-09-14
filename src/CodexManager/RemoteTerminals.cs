using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

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
    private readonly Dictionary<string, Shell> shells = [];
    private bool disposed;
    public async Task<JsonNode?> Handle(JsonObject request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string Text(string key) => request[key]?.GetValue<string>() ?? "";
        var method = Text("method");
        if (method == "terminal/open")
        {
            var workspace = workspaces.FirstOrDefault(w => w.Id == Text("workspaceId")) ?? throw new IOException("Select a workspace on the host first.");
            foreach (var stale in shells.Where(s => s.Value.Exited).Select(s => s.Key).ToArray()) { shells[stale].Dispose(); shells.Remove(stale); }
            var existing = shells.FirstOrDefault(s => s.Value.WorkspaceId == workspace.Id);
            if (existing.Value is not null) return new JsonObject { ["id"] = existing.Key };
            if (shells.Count >= 16) throw new IOException("Close an existing remote terminal before opening another.");
            var shell = new Shell { WorkspaceId = workspace.Id }; var id = "remote:" + workspace.Id; shells.Add(id, shell);
            try { await shell.Session.Start(workspace, settings: store, durableId: id, resumeOnly: request["resumeOnly"]?.GetValue<bool>() == true); }
            catch { shells.Remove(id); shell.Dispose(); throw; }
            return new JsonObject { ["id"] = id };
        }
        var requestedId = Text("terminalId");
        // A client may reconnect after the host UI restarted. Reattach its
        // stable workspace terminal to the surviving helper before reading it.
        if (!shells.ContainsKey(requestedId) && method != "terminal/close" && requestedId.StartsWith("remote:", StringComparison.Ordinal)
            && workspaces.Any(w => w.Id == requestedId[7..]))
            await Handle(new JsonObject { ["method"] = "terminal/open", ["workspaceId"] = requestedId[7..], ["resumeOnly"] = true });
        if (!shells.TryGetValue(requestedId, out var active)) throw new IOException("This terminal has closed. Open it again to start a new shell.");
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
                await active.Session.Close(); active.Dispose(); shells.Remove(Text("terminalId")); break;
            default: throw new IOException("Unknown terminal operation.");
        }
        return JsonValue.Create(true);
    }
    public void Dispose() { disposed = true; foreach (var shell in shells.Values) shell.Dispose(); shells.Clear(); }
}
