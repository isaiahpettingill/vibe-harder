using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed class SessionService(Store store, IList<Workspace> workspaces, IList<Chat> chats, Func<Chat, Workspace, ChatRuntime> runtime) : IDisposable
{
    private readonly RemoteTerminals terminals = new(store, workspaces);
    private sealed record MessageSnapshot(string Text, Attachment[] Attachments, string Revision);
    private readonly Dictionary<string, MessageSnapshot> messageSnapshots = [];
    private JsonNode MessageRow(Message message, JsonObject? known)
    {
        var attachments = message.Attachments.ToArray();
        if (!messageSnapshots.TryGetValue(message.Id, out var snapshot) || snapshot.Text != message.Text || !snapshot.Attachments.SequenceEqual(attachments))
        {
            if (messageSnapshots.Count >= 256) messageSnapshots.Clear();
            snapshot = new(message.Text, attachments, Guid.NewGuid().ToString("N")); messageSnapshots[message.Id] = snapshot;
        }
        var row = new JsonObject { ["id"] = message.Id, ["revision"] = snapshot.Revision };
        if (known?[message.Id]?.GetValue<string>() == snapshot.Revision) return row;
        row["sequence"] = message.Sequence; row["role"] = message.Role; row["text"] = message.Text;
        row["attachments"] = JsonSerializer.SerializeToNode(attachments, StoreJsonContext.Default.AttachmentArray);
        return row;
    }
    public void Dispose() => terminals.Dispose();
    public event Action? Changed;
    private readonly Dictionary<string, (JsonObject Request, TaskCompletionSource<JsonObject> Completion)> permissions = [];
    public string RegisterPermission(Chat chat, JsonElement request, TaskCompletionSource<JsonObject> completion)
    {
        var id = Guid.NewGuid().ToString("N");
        var value = JsonNode.Parse(request.GetRawText())!.AsObject(); value["chatId"] = chat.Id; value["chatTitle"] = chat.Title; value["id"] = id;
        permissions[id] = (value, completion); return id;
    }
    public void ForgetPermission(string id) => permissions.Remove(id);
    public async Task<JsonObject> Permission(Chat chat, JsonElement request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = RegisterPermission(chat, request, completion);
        using var cancel = token.Register(() => completion.TrySetResult(RpcJson.Permission()));
        try { return await completion.Task; } finally { permissions.Remove(id); }
    }
    public async Task<JsonNode?> Handle(JsonObject request)
    {
        var result = await HandleCore(request);
        if (result is JsonObject summary && summary["id"] is JsonValue id && chats.FirstOrDefault(c => c.Id == id.GetValue<string>()) is { } chat)
            summary["preparing"] = runtime(chat, workspaces.Single(w => w.Id == chat.WorkspaceId)).IsPreparing;
        // A successful mutation reply must survive an immediate host restart.
        var method = request["method"]?.GetValue<string>() ?? "";
        if (method is not ("list" or "chat") && !method.StartsWith("terminal/", StringComparison.Ordinal)) await store.FlushAsync();
        return result;
    }
    private async Task<JsonNode?> HandleCore(JsonObject request)
    {
        string Text(string key) => request[key]?.GetValue<string>() ?? "";
        var method = Text("method");
        if (method.StartsWith("terminal/", StringComparison.Ordinal)) return await terminals.Handle(request);
        if (method == "locations") return new JsonObject { ["distros"] = new JsonArray((await Hosts.Distros()).Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()) };
        if (method == "directories")
        {
            var distro = string.IsNullOrWhiteSpace(Text("distro")) ? null : Text("distro");
            var path = Text("path");
            if (path.Length == 0) path = distro is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : (await Hosts.Capture(Hosts.Info("wsl.exe", "-d", distro, "--exec", "sh", "-c", "printf '%s' \"$HOME\""))).Trim();
            var directories = await Hosts.Directories(distro, path);
            var parent = distro is null ? Directory.GetParent(path)?.FullName ?? path : path.TrimEnd('/').LastIndexOf('/') is > 0 and var i ? path[..i] : "/";
            return new JsonObject { ["path"] = path, ["parent"] = parent, ["directories"] = new JsonArray(directories.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()) };
        }
        if (method == "list") return new JsonObject
        {
            ["providers"] = new JsonArray(AgentProviders.Enabled(store).Select(p => (JsonNode)JsonValue.Create(p.Provider.ToString())!).ToArray()),
            ["permissions"] = new JsonArray(permissions.Values.Select(p => (JsonNode)p.Request.DeepClone()).ToArray()),
            ["platform"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "apple" : "linux",
            ["workspaces"] = new JsonArray(workspaces.Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["name"] = w.Name, ["path"] = w.Path, ["distro"] = w.Distro }).ToArray()),
            ["chats"] = new JsonArray(chats.Select(c => (JsonNode)Summary(c)).ToArray())
        };
        if (method == "workspace")
        {
            var path = Text("path"); var distro = Text("distro");
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Enter a workspace path on the host.");
            var workspace = new Workspace(Guid.NewGuid().ToString("N"), Text("name"), path, string.IsNullOrWhiteSpace(distro) ? null : distro);
            await Hosts.Validate(workspace);
            var existing = workspaces.FirstOrDefault(w => w.Path == path && w.Distro == workspace.Distro);
            if (existing is not null) return JsonValue.Create(existing.Id);
            workspaces.Add(workspace); store.Save(workspace); Changed?.Invoke(); return JsonValue.Create(workspace.Id);
        }
        if (method == "create")
        {
            var owner = workspaces.Single(w => w.Id == Text("workspaceId"));
            if (!Enum.TryParse<AgentProvider>(Text("provider"), out var provider) || !Enum.IsDefined(provider)) throw new IOException("Unknown provider.");
            if (!AgentProviders.IsEnabled(store, provider)) throw new IOException("Enable this provider in Settings > Additional agents on the host.");
            var created = new Chat { WorkspaceId = owner.Id, Provider = provider, RetainHistory = false }; chats.Add(created); store.Save(created); Changed?.Invoke(); return Summary(created);
        }
        if (method == "import")
        {
            var owner = workspaces.Single(w => w.Id == Text("workspaceId"));
            var provider = Enum.Parse<AgentProvider>(Text("provider"));
            if (!AgentProviders.IsEnabled(store, provider)) throw new IOException("Enable this provider in Settings > Additional agents on the host.");
            var found = await ChatHistory.Discover(owner, AgentProviders.Command(store, owner, provider), provider: provider);
            foreach (var imported in found.Where(c => !chats.Any(saved => saved.WorkspaceId == owner.Id && saved.Provider == provider && saved.SessionId == c.SessionId) && store.Setting(AgentProviders.HiddenHistoryKey(c)) != "1"))
            { chats.Add(imported); store.Save(imported); }
            Changed?.Invoke(); return JsonValue.Create(found.Count);
        }
        if (method == "approve")
        {
            if (!permissions.TryGetValue(Text("permissionId"), out var pending)) throw new IOException("This approval is no longer pending.");
            var option = Text("optionId");
            if (!pending.Request["options"]!.AsArray().Any(o => o?["optionId"]?.GetValue<string>() == option)) throw new IOException("Unknown approval option.");
            pending.Completion.TrySetResult(RpcJson.Permission(option)); return JsonValue.Create(true);
        }
        var chat = chats.Single(c => c.Id == Text("chatId")); var workspaceOwner = workspaces.Single(w => w.Id == chat.WorkspaceId);
        if (method == "file/read") return await FileLinks.Read(request, workspaceOwner);
        if (method == "file/download") return new JsonObject { ["path"] = FileLinks.Resolve(Text("path"), workspaceOwner) };
        var active = runtime(chat, workspaceOwner);
        if (active.IsChangingHistory && method != "chat") throw new IOException("Wait for the history change to finish.");
        if (method == "history/options") return await active.HistoryOptions(request["messageId"]?.GetValue<string>(), request["sequence"]?.GetValue<int>() ?? -1);
        if (method == "history/branch")
        {
            var branch = await active.BranchHistory(request["messageId"]?.GetValue<string>(), request["sequence"]?.GetValue<int>() ?? -1, request["checkpoint"]?.GetValue<string>(), request["fork"]?.GetValue<bool>() ?? false);
            if (!ReferenceEquals(branch, chat)) chats.Add(branch);
            var input = new PendingInput(Text("text"), request["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? []);
            if (!string.IsNullOrWhiteSpace(input.Text) || input.Attachments.Length > 0) _ = runtime(branch, workspaceOwner).Send(input.Text, input.Attachments);
            Changed?.Invoke(); return Summary(branch);
        }
        if (method == "chat")
        {
            active.KeepAlive();
            Message[] page;
            if (request["before"] is not null) page = await store.ReadPageAsync(chat, request["before"]!.GetValue<int>(), limit: 50);
            else if (request["after"] is not null) page = await store.ReadPageAsync(chat, request["after"]!.GetValue<int>(), limit: 50, newer: true);
            else if (!chat.RetainHistory)
            {
                foreach (var message in chat.Messages) store.SaveMessage(chat, message);
                var saved = await store.ReadPageAsync(chat);
                page = saved.Concat(chat.Messages).GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).TakeLast(Chat.HistoryPageSize).ToArray();
            }
            else
            {
                if (!chat.HistoryLoaded && !chat.Busy) store.ApplyRecentPage(chat, await store.ReadPageAsync(chat));
                page = chat.Messages.ToArray();
            }
            // New clients distinguish explicit selection from restoring a cached selection.
            // Legacy clients retain their history-load behavior when the flag is absent.
            if (request["activate"]?.GetValue<bool>() != false && chat.SessionId is not null && !chat.Busy)
            {
                if (page.Length == 0 || store.Setting("historyIncomplete:" + chat.Id) == "1") _ = active.LoadHistory();
                else if (request["activate"]?.GetValue<bool>() == true && !active.IsConnected && !active.IsReconnecting) _ = active.Reconnect();
            }
            var result = Summary(chat);
            var known = request["knownMessages"] as JsonObject;
            if (known?.Count > Chat.HistoryPageSize) throw new IOException("Too many message revisions.");
            result["messages"] = new JsonArray(page.Select(m => MessageRow(m, known)).ToArray());
            result["permissions"] = new JsonArray(permissions.Values.Where(p => p.Request["chatId"]!.GetValue<string>() == chat.Id).Select(p => (JsonNode)p.Request.DeepClone()).ToArray());
            result["commands"] = new JsonArray(chat.Commands.Select(c => (JsonNode)JsonValue.Create("/" + c.Name)!).ToArray());
            result["commandOptions"] = new JsonArray(chat.Commands.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["hint"] = c.Hint }).ToArray());
            result["config"] = new JsonArray(chat.ConfigOptions.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["current"] = c.Current, ["values"] = new JsonArray(c.Values.Select(v => (JsonNode)new JsonObject { ["value"] = v.Value, ["name"] = v.Name }).ToArray()) }).ToArray());
            result["canSteer"] = active.SupportsSteering && active.IsPrompting && !active.IsSteering;
            result["recentModels"] = new JsonArray(ModelPicker.Recent(store, chat.Provider).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
            result["queue"] = new JsonArray(chat.QueuedInputs.Select(q => (JsonNode)new JsonObject { ["id"] = q.Id, ["text"] = q.Text, ["attachments"] = q.Attachments.Length }).ToArray());
            return result;
        }
        if (method == "queue/advance") { await active.AdvanceQueued(); return Summary(chat); }
        if (method is "queue/steer" or "queue/remove" or "queue/edit" or "queue/send")
        {
            var queued = chat.QueuedInputs.FirstOrDefault(q => q.Id == Text("queueId")) ?? throw new IOException("This message is no longer queued.");
            if (method == "queue/remove") active.RemoveQueued(queued);
            else if (method == "queue/edit") active.EditQueued(queued, Text("text"));
            else if (method == "queue/send") await active.SendQueuedNow(queued, waitForCompletion: false);
            else if (await active.Steer(queued)) active.RemoveQueued(queued);
            else throw new IOException("Steering was not accepted. The message remains queued.");
            return Summary(chat);
        }
        if (method is "send" or "queue" or "steer")
        {
            var attachments = request["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? [];
            var input = new PendingInput(Text("text"), attachments);
            if (string.IsNullOrWhiteSpace(input.Text) && input.Attachments.Length == 0) throw new IOException("Enter a message.");
            if (chat.Title == "New chat" && input.Text.Length > 0) { chat.Title = input.Text[..Math.Min(80, input.Text.Length)].Replace('\n', ' '); store.Save(chat); Changed?.Invoke(); }
            if (method == "steer") return JsonValue.Create(await active.Steer(input));
            if (method == "queue" || chat.Busy || active.IsRecovering) active.Queue(input); else _ = active.Send(input.Text, input.Attachments);
        }
        else if (method == "stop") await active.Stop();
        else if (method == "rename") { chat.Title = Text("title"); store.Save(chat); Changed?.Invoke(); }
        else if (method == "read") { chat.HasUnreadCompletion = false; store.Save(chat); }
        else if (method == "archive") { chat.Archived = request["archived"]?.GetValue<bool>() ?? true; store.Save(chat); Changed?.Invoke(); }
        else if (method == "reconnect") await active.Reconnect();
        else if (method == "resume")
        {
            if (chat.Busy) throw new IOException("The chat is already running.");
            if (chat.InterruptedInput is not { } input) throw new IOException("This chat has no interrupted request.");
            chat.InterruptedInput = null;
            _ = active.Send(" ", []);
        }
        else if (method == "config") await active.SetConfig(chat.ConfigOptions.Single(c => c.Id == Text("configId")), Text("value"));
        else throw new IOException("Unknown remote operation.");
        return Summary(chat);
    }
    private static JsonObject Summary(Chat c) => new() { ["id"] = c.Id, ["workspaceId"] = c.WorkspaceId, ["title"] = c.Title, ["provider"] = c.Provider.ToString(), ["busy"] = c.Busy, ["needsPermission"] = c.NeedsPermission, ["unread"] = c.HasUnreadCompletion, ["status"] = c.Status, ["archived"] = c.Archived, ["queued"] = c.QueuedInputs.Count, ["interrupted"] = c.InterruptedInput is not null };
}
