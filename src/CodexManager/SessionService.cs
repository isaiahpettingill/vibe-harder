using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed class SessionService(Store store, IList<Workspace> workspaces, IList<Chat> chats, Func<Chat, Workspace, ChatRuntime> runtime)
{
    public event Action? Changed;
    private readonly Dictionary<string, (JsonObject Request, TaskCompletionSource<JsonObject> Completion)> permissions = [];
    public string RegisterPermission(Chat chat, JsonElement request, TaskCompletionSource<JsonObject> completion)
    {
        var id = Guid.NewGuid().ToString("N");
        var value = JsonNode.Parse(request.GetRawText())!.AsObject(); value["chatId"] = chat.Id; value["id"] = id;
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
        string Text(string key) => request[key]?.GetValue<string>() ?? "";
        var method = Text("method");
        if (method == "list") return new JsonObject
        {
            ["workspaces"] = new JsonArray(workspaces.Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["name"] = w.Name, ["path"] = w.Path, ["distro"] = w.Distro }).ToArray()),
            ["chats"] = new JsonArray(chats.Select(c => (JsonNode)Summary(c)).ToArray())
        };
        if (method == "workspace")
        {
            var path = Text("path"); var distro = Text("distro");
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Enter a workspace path on the host.");
            var workspace = new Workspace(Guid.NewGuid().ToString("N"), Text("name"), path, string.IsNullOrWhiteSpace(distro) ? null : distro);
            if (!workspace.IsWsl && !Directory.Exists(path)) throw new IOException("That folder does not exist on the host.");
            workspaces.Add(workspace); store.Save(workspace); Changed?.Invoke(); return JsonValue.Create(workspace.Id);
        }
        if (method == "create")
        {
            var owner = workspaces.Single(w => w.Id == Text("workspaceId"));
            if (!Enum.TryParse<AgentProvider>(Text("provider"), out var provider) || !Enum.IsDefined(provider)) throw new IOException("Unknown provider.");
            var created = new Chat { WorkspaceId = owner.Id, Provider = provider }; chats.Add(created); store.Save(created); Changed?.Invoke(); return Summary(created);
        }
        if (method == "import")
        {
            var owner = workspaces.Single(w => w.Id == Text("workspaceId"));
            var provider = Enum.Parse<AgentProvider>(Text("provider"));
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
        var active = runtime(chat, workspaceOwner);
        if (method == "chat")
        {
            if (chat.Messages.Count == 0 && chat.SessionId is not null && !chat.Busy) await active.LoadHistory();
            var result = Summary(chat);
            result["messages"] = new JsonArray(chat.Messages.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["role"] = m.Role, ["text"] = m.Text, ["attachments"] = JsonSerializer.SerializeToNode(m.Attachments.ToArray(), StoreJsonContext.Default.AttachmentArray) }).ToArray());
            result["permissions"] = new JsonArray(permissions.Values.Where(p => p.Request["chatId"]!.GetValue<string>() == chat.Id).Select(p => (JsonNode)p.Request.DeepClone()).ToArray());
            result["commands"] = new JsonArray(chat.Commands.Select(c => (JsonNode)JsonValue.Create("/" + c.Name)!).ToArray());
            result["config"] = new JsonArray(chat.ConfigOptions.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["current"] = c.Current, ["values"] = new JsonArray(c.Values.Select(v => (JsonNode)new JsonObject { ["value"] = v.Value, ["name"] = v.Name }).ToArray()) }).ToArray());
            return result;
        }
        if (method is "send" or "queue" or "steer")
        {
            var attachments = request["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? [];
            var input = new PendingInput(Text("text"), attachments);
            if (string.IsNullOrWhiteSpace(input.Text) && input.Attachments.Length == 0) throw new IOException("Enter a message.");
            if (chat.Title == "New chat" && input.Text.Length > 0) { chat.Title = input.Text[..Math.Min(80, input.Text.Length)].Replace('\n', ' '); store.Save(chat); Changed?.Invoke(); }
            if (method == "steer") return JsonValue.Create(await active.Steer(input));
            if (method == "queue" || chat.Busy) active.Queue(input); else _ = active.Send(input.Text, input.Attachments);
        }
        else if (method == "stop") await active.Stop();
        else if (method == "rename") { chat.Title = Text("title"); store.Save(chat); Changed?.Invoke(); }
        else if (method == "archive") { chat.Archived = request["archived"]?.GetValue<bool>() ?? true; store.Save(chat); Changed?.Invoke(); }
        else if (method == "reconnect") await active.Reconnect();
        else if (method == "resume")
        {
            if (chat.Busy) throw new IOException("The chat is already running.");
            if (chat.InterruptedInput is not { } input) throw new IOException("This chat has no interrupted request.");
            chat.InterruptedInput = null;
            _ = active.Send("Continue the interrupted request. Inspect saved history and current state; do not repeat completed actions.\n\n" + input.Text, input.Attachments);
        }
        else if (method == "config") await active.SetConfig(chat.ConfigOptions.Single(c => c.Id == Text("configId")), Text("value"));
        else throw new IOException("Unknown remote operation.");
        return Summary(chat);
    }
    private static JsonObject Summary(Chat c) => new() { ["id"] = c.Id, ["workspaceId"] = c.WorkspaceId, ["title"] = c.Title, ["provider"] = c.Provider.ToString(), ["busy"] = c.Busy, ["status"] = c.Status, ["archived"] = c.Archived, ["queued"] = c.QueuedInputs.Count, ["interrupted"] = c.InterruptedInput is not null };
}
