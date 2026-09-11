using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Threading;

namespace CodexManager;

public sealed class ChatRuntime(Chat chat, Workspace workspace, Store store, string command) : IAsyncDisposable
{
    private AcpClient? client;
    private bool loading;
    private bool replaying;
    private bool connected;
    private readonly Dictionary<string, string> activeToolInputs = [];
    private bool detachedTurn;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? turn;
    private Task? activeTask;
    private Task? reconnectTask;
    private Task? recoveryTask;
    private CancellationTokenSource? recoveryCancellation;
    public bool IsRecovering { get; private set; }
    private Task<bool>? steeringTask;
    private bool reconnecting;
    private int rapidDisconnects;
    private DateTimeOffset connectedAt;
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? Permission { get; set; }
    public event Action? Changed;
    public bool IsLoadingHistory { get; private set; }
    public bool IsReconnecting => reconnecting;
    public bool IsPrompting => (turn is not null || detachedTurn) && chat.Busy;
    public bool IsConfiguring { get; private set; }
    public bool IsConnected => connected && client?.Alive == true;
    public bool SupportsSteering { get; private set; }
    public bool IsSteering { get; private set; }
    public void Queue(PendingInput input) { chat.QueuedInputs.Add(input); store.Save(chat); Changed?.Invoke(); }
    public void RemoveQueued(PendingInput input) { chat.QueuedInputs.Remove(input); store.Save(chat); Changed?.Invoke(); }
    public Task<bool> Steer(PendingInput input) => IsSteering ? Task.FromResult(false) : steeringTask = SteerCore(input);
    private async Task<bool> SteerCore(PendingInput input)
    {
        if (!SupportsSteering || !IsPrompting || IsSteering || client is null) return false;
        IsSteering = true; store.Setting("steering:" + chat.Id, JsonSerializer.Serialize(input, StoreJsonContext.Default.PendingInput)); Changed?.Invoke();
        try
        {
            var content = new JsonArray();
            if (!string.IsNullOrWhiteSpace(input.Text)) content.Add((JsonNode)RpcJson.Object(("type", "text"), ("text", input.Text)));
            foreach (var a in input.Attachments) content.Add((JsonNode)a.ToContent());
            var result = await client.Request("_session/steering", RpcJson.Object(("sessionId", chat.SessionId), ("prompt", content),
                ("_meta", RpcJson.Object(("steering", RpcJson.Object(("idleBehavior", "promptRequired")))))), lifetime.Token);
            var outcome = result.GetProperty("outcome").GetString();
            if (outcome == "promptRequired") return false;
            if (outcome is not "injected" and not "startedNewTurn") throw new IOException("Provider did not accept steering: " + outcome);
            if (outcome == "startedNewTurn")
            {
                // Older adapters can win the idle race and start an unowned
                // prompt. They do not report its completion. Keep Stop available
                // and pause the queue instead of pretending that turn is idle.
                detachedTurn = true; chat.Busy = true;
                chat.Status = "Steered turn running — this adapter does not report its completion; Stop before sending another turn";
                store.Setting("interrupted:" + chat.Id, JsonSerializer.Serialize(input, StoreJsonContext.Default.PendingInput));
            }
            var message = new Message { Role = "user", Provider = chat.Provider, Text = input.Text };
            foreach (var a in input.Attachments) message.Attachments.Add(a);
            chat.Messages.Add(message); store.SaveMessage(chat, message);
            return true;
        }
        catch (Exception error) { chat.Status = "Could not steer: " + error.Message; return false; }
        finally { store.Setting("steering:" + chat.Id, ""); IsSteering = false; Changed?.Invoke(); }
    }
    public async Task SendQueuedNow(PendingInput input)
    {
        if (!chat.QueuedInputs.Contains(input)) return;
        if (IsPrompting) await Stop();
        if (chat.Busy || lifetime.IsCancellationRequested) return;
        RemoveQueued(input); await Send(input.Text, input.Attachments);
    }
    private void Configure(JsonElement response)
    {
        if (!response.TryGetProperty("configOptions", out _) && !response.TryGetProperty("models", out _) && !response.TryGetProperty("modes", out _)) return;
        chat.ConfigOptions = SessionConfig.Read(response); chat.ConfigVersion++; Changed?.Invoke();
    }
    public async Task SetConfig(SessionConfig config, string value)
    {
        if (chat.Busy || IsConfiguring || client is null || chat.SessionId is null) return;
        if (!config.Values.Any(v => v.Value == value)) return;
        IsConfiguring = true; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var parameters = RpcJson.Object(("sessionId", chat.SessionId));
            var method = "session/set_config_option";
            if (config.Kind is "model" or "mode") { method = "session/set_" + config.Kind; parameters[config.Kind + "Id"] = value; }
            else { parameters["configId"] = config.Id; parameters["value"] = config.Kind == "boolean" ? JsonValue.Create(value == "true") : JsonValue.Create(value); if (config.Kind == "boolean") parameters["type"] = "boolean"; }
            var result = await client.Request(method, parameters, timeout.Token);
            chat.ConfigOptions = chat.ConfigOptions.Select(c => c.Id == config.Id ? c with { Current = value } : c).ToArray(); chat.ConfigVersion++;
            Configure(result);
        }
        catch (Exception error) { chat.Status = "Could not change " + config.Name + ": " + error.Message; chat.ConfigVersion++; }
        finally { IsConfiguring = false; Changed?.Invoke(); }
    }
    private async Task ConnectWithRecovery(bool replayHistory, CancellationToken token)
    {
        var previous = chat.Messages.ToArray();
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await Connect(replayHistory); return; }
            catch (Exception error) when (attempt < 5 && error is not OperationCanceledException && !AgentProviders.IsAuthenticationError(error) && !chat.NeedsLogin && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
            {
                if (replayHistory) { chat.Messages.Clear(); foreach (var message in previous) chat.Messages.Add(message); }
                chat.Status = $"Reconnecting ({attempt}/4)…"; Changed?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), token);
            }
        }
    }
    public Task Reconnect(bool automatic = false)
    {
        if (reconnecting) return reconnectTask ?? Task.CompletedTask;
        if (!automatic || DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) rapidDisconnects = 0;
        if (automatic && ++rapidDisconnects > 4)
        {
            chat.Status = "Agent keeps exiting — check connection settings, then reconnect"; Changed?.Invoke(); return Task.CompletedTask;
        }
        return reconnectTask = ReconnectCore();
    }
    private async Task ReconnectCore()
    {
        detachedTurn = false;
        reconnecting = true; connected = false;
        try
        {
            turn?.Cancel();
            if (client is not null) await client.DisposeAsync();
            if (activeTask is not null) await activeTask;
            if (lifetime.IsCancellationRequested) return;
            if (chat.SessionId is not null && chat.Messages.Count == 0) { await LoadHistory(); return; }
            chat.Busy = true; chat.Status = "Reconnecting…"; Changed?.Invoke();
            await ConnectWithRecovery(false, recoveryCancellation?.Token ?? lifetime.Token); chat.Status = "Ready";
        }
        catch (OperationCanceledException) { chat.Status = lifetime.IsCancellationRequested ? "Disconnected" : "Reconnect timed out or was cancelled — try reconnecting"; }
        catch (Exception error) { chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error); chat.Status = "Reconnect failed: " + error.Message; }
        finally { chat.Busy = false; reconnecting = false; Changed?.Invoke(); }
    }
    public async Task Connect(bool replayHistory = false)
    {
        if (connected && client?.Alive == true) return;
        connected = false;
        if (client is not null) await client.DisposeAsync();
        chat.Commands = []; Changed?.Invoke();
        client = new(Hosts.Agent(workspace, command));
        var connection = client;
        client.AuthenticationChanged += needsLogin => Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested && ReferenceEquals(client, connection)) { chat.NeedsLogin = needsLogin; Changed?.Invoke(); } });
        client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (!connected || !ReferenceEquals(client, connection) || lifetime.IsCancellationRequested) return;
            connected = false;
            if (!chat.Busy && !chat.NeedsLogin) _ = Reconnect(true);
        });
        client.UpdateAsync = async update => await Dispatcher.UIThread.InvokeAsync(() => Update(update), DispatcherPriority.Background);
        client.PermissionRequested = async (request, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, turn?.Token ?? lifetime.Token);
            return Permission is null ? RpcJson.Permission() : await Permission(request, linked.Token);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, turn?.Token ?? lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var init = await client.Initialize(timeout.Token);
        SupportsSteering = init.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("steering", out var steering) && steering.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;
        loading = chat.SessionId is not null;
        replaying = loading && replayHistory;
        try
        {
            if (loading)
            {
                if (!init.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean()) throw new IOException("This adapter does not support session resume. Update its connection command.");
                try { Configure(await client.Request("session/load", RpcJson.Object(("sessionId", chat.SessionId), ("cwd", workspace.Path), ("mcpServers", new JsonArray())), timeout.Token)); }
                catch (AcpException error) when (chat.Provider == AgentProvider.Codex && error.Message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase)
                    && store.Setting("unmaterialized:" + chat.Id) == chat.SessionId && chat.Messages.All(m => m.Role == "system"))
                {
                    // A new Codex session may exist only in the adapter process until
                    // its first prompt. Replace only sessions we know never received one.
                    loading = false; replaying = false;
                    await NewSession(timeout.Token);
                }
            }
            else
            {
                await NewSession(timeout.Token);
            }
            connected = true; connectedAt = DateTimeOffset.UtcNow;
        }
        finally { loading = false; replaying = false; }
    }
    private async Task NewSession(CancellationToken token)
    {
        var session = await client!.Request("session/new", RpcJson.Object(("cwd", workspace.Path), ("mcpServers", new JsonArray())), token);
        chat.SessionId = session.GetProperty("sessionId").GetString();
        store.Setting("unmaterialized:" + chat.Id, chat.SessionId!); store.Save(chat); Configure(session);
    }
    public Task LoadHistory() => chat.Busy ? activeTask ?? Task.CompletedTask : activeTask = LoadHistoryCore();
    private async Task LoadHistoryCore()
    {
        chat.Busy = true; IsLoadingHistory = true; chat.Status = "Loading history…"; Changed?.Invoke();
        var previous = chat.Messages.ToArray();
        store.Setting("historyIncomplete:" + chat.Id, "1"); store.ClearHistory(chat);
        try
        {
            await ConnectWithRecovery(true, lifetime.Token);
            for (var i = 0; i < chat.Messages.Count; i++)
            {
                lifetime.Token.ThrowIfCancellationRequested(); store.SaveMessage(chat, chat.Messages[i]);
                if (i % 64 == 0) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            store.Setting("historyIncomplete:" + chat.Id, "0"); await store.FlushAsync(); chat.HistoryLoaded = true; chat.Status = "Ready";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { chat.Status = "Ready"; }
        catch (Exception error)
        {
            chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error);
            chat.Messages.Clear(); foreach (var message in previous) chat.Messages.Add(message);
            chat.Status = "History unavailable: " + error.Message;
        }
        finally { chat.Busy = false; IsLoadingHistory = false; Changed?.Invoke(); }
    }
    public Task Send(string text, Attachment[] attachments) => reconnecting ? reconnectTask ?? Task.CompletedTask : chat.Busy ? activeTask ?? Task.CompletedTask : activeTask = SendCore(text, attachments);
    private async Task SendCore(string text, Attachment[] attachments)
    {
        if (chat.Busy) return;
        chat.HasUnreadCompletion = false; chat.Busy = true; chat.Status = "Connecting…"; Changed?.Invoke();
        turn = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        chat.PendingInput = new(text, attachments); store.Save(chat);
        store.Setting("interrupted:" + chat.Id, JsonSerializer.Serialize(chat.PendingInput, StoreJsonContext.Default.PendingInput));
        var recoverConnection = false;
        var completed = false;
        try
        {
            if (!chat.HistoryLoaded) store.ApplyRecentPage(chat, await store.ReadPageAsync(chat, limit: chat.RetainHistory ? Chat.HistoryPageSize : 1, token: turn.Token));
            await store.FlushAsync();
            await ConnectWithRecovery(chat.Messages.Count == 0, turn.Token);
            turn.Token.ThrowIfCancellationRequested();
            var user = new Message { Role = "user", Provider = chat.Provider, Text = text + string.Concat(attachments.Select(a => $"\n\n📎 {a.Name}")) };
            foreach (var a in attachments) user.Attachments.Add(a);
            chat.Messages.Add(user); store.SaveMessage(chat, user);
            chat.Status = "Working…"; Changed?.Invoke();
            var content = new JsonArray();
            if (!string.IsNullOrWhiteSpace(text)) content.Add((JsonNode)RpcJson.Object(("type", "text"), ("text", text)));
            foreach (var attachment in attachments) content.Add((JsonNode)attachment.ToContent());
            store.Setting("unmaterialized:" + chat.Id, "");
            var result = await client!.Request("session/prompt", RpcJson.Object(("sessionId", chat.SessionId), ("prompt", content)), lifetime.Token);
            chat.Status = result.TryGetProperty("stopReason", out var reason) && reason.GetString() == "cancelled" ? "Interrupted" : "Ready";
            completed = chat.Status == "Ready" && !turn.IsCancellationRequested;
            if (completed) { chat.InterruptedInput = null; chat.HasUnreadCompletion = true; }

        }
        catch (OperationCanceledException)
        {
            chat.Status = "Interrupted";
            RestoreInput(text, attachments);
        }
        catch (Exception error)
        {
            chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error);
            recoverConnection = client?.Alive != true && !turn.IsCancellationRequested && !chat.NeedsLogin;
            chat.InterruptedInput = new(text, attachments);
            chat.Status = "Connection error";
            Add("system", "**Could not complete the turn.**\n\n" + error.Message + $"\n\nCheck the {AgentProviders.Get(chat.Provider).Name} adapter command and authentication in this workspace’s environment.");
            // Keep failed input available to retry, including attachments.
            RestoreInput(text, attachments);
        }
        finally
        {
            chat.Updated = DateTimeOffset.UtcNow;
            chat.PendingInput = null;
            foreach (var message in chat.Messages) store.SaveMessage(chat, message);
            store.Save(chat); turn.Dispose(); turn = null;
            try
            {
                await store.FlushAsync();
                if (completed && !lifetime.IsCancellationRequested && !detachedTurn) { store.Setting("interrupted:" + chat.Id, ""); await store.FlushAsync(); }
            }
            catch (Exception error) { completed = false; chat.Status = "Could not save completed turn: " + error.Message; }
            chat.Busy = detachedTurn;
            if (!chat.RetainHistory) store.ReleaseHistory(chat);
            activeToolInputs.Clear(); Changed?.Invoke();
            if (recoverConnection && !lifetime.IsCancellationRequested && !IsRecovering)
                Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested && chat.InterruptedInput is { } input) recoveryTask = RecoverConnection(input); });
            if (completed && !IsSteering && !lifetime.IsCancellationRequested && chat.QueuedInputs.FirstOrDefault() is { } next)
                Dispatcher.UIThread.Post(async () => { if (!lifetime.IsCancellationRequested && !chat.Busy && chat.QueuedInputs.Contains(next)) { RemoveQueued(next); await Send(next.Text, next.Attachments); } });
        }
    }
    private void RestoreInput(string text, Attachment[] attachments)
    {
        chat.RecoverInput(new(text, attachments));
    }
    private async Task RecoverConnection(PendingInput input)
    {
        IsRecovering = true; recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = recoveryCancellation.Token;
        try
        {
            var attempt = 0;
            while (!token.IsCancellationRequested)
            {
                chat.Status = $"Recovering connection (attempt {++attempt})…"; Changed?.Invoke();
                await Reconnect();
                if (token.IsCancellationRequested) return;
                if (chat.NeedsLogin) { chat.Status = "Interrupted — sign in to resume"; return; }
                if (IsConnected)
                {
                    if (store.Setting("autoResume") != "1") { chat.Status = "Interrupted — resume required"; return; }
                    if (chat.Draft == input.Text) chat.Draft = "";
                    foreach (var attachment in input.Attachments) chat.Attachments.Remove(attachment);
                    await Send("Continue the interrupted request below. Inspect saved history and current workspace state before taking action; do not repeat completed actions.\n\n" + input.Text, input.Attachments);
                    if (chat.InterruptedInput is null || IsConnected || token.IsCancellationRequested) return;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), token);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsRecovering = false; recoveryCancellation.Dispose(); recoveryCancellation = null; if (!lifetime.IsCancellationRequested) { if (token.IsCancellationRequested) chat.Status = "Interrupted — automatic recovery stopped"; Changed?.Invoke(); } }
    }
    public async Task Stop()
    {
        recoveryCancellation?.Cancel(); chat.InterruptedInput = null;
        store.Setting("interrupted:" + chat.Id, "");
        if (IsRecovering && turn is null && client is not null) await client.DisposeAsync();
        if (detachedTurn)
        {
            detachedTurn = false;
            if (client?.Alive == true && chat.SessionId is not null) await client.Notify("session/cancel", RpcJson.Object(("sessionId", chat.SessionId)));
            connected = false;
            if (client is not null) await client.DisposeAsync();
            if (activeTask is not null) await activeTask;
            chat.Busy = false; chat.Status = "Interrupted"; Changed?.Invoke(); return;
        }
        // Reading a saved transcript is not an agent turn. Never cancel the
        // provider's session merely because its history is being replayed.
        if (turn is null) return;
        var activeTurn = turn;
        var promptTask = activeTask;
        turn?.Cancel();
        if (client?.Alive == true && chat.SessionId is not null)
        {
            await client.Notify("session/cancel", RpcJson.Object(("sessionId", chat.SessionId)));
            if (chat.Busy && ReferenceEquals(turn, activeTurn)) { chat.Status = "Interrupting…"; Changed?.Invoke(); }
            // A broken adapter must not leave a chat permanently busy.
            await Task.WhenAny(promptTask ?? Task.CompletedTask, Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token));
            if (chat.Busy && ReferenceEquals(turn, activeTurn)) await client.DisposeAsync();
        }
        else if (client is not null) await client.DisposeAsync();
    }
    private void Add(string role, string text, string? toolId = null)
    {
        if (replaying && chat.Messages.LastOrDefault() is { } previous) store.SaveMessage(chat, previous);
        var m = new Message { Role = role, Provider = chat.Provider, Text = text, ToolId = toolId, Sequence = chat.NextSequence++ };
        chat.Messages.Add(m); store.TrimHistory(chat); if (!replaying) store.SaveMessage(chat, m);
    }
    private async Task Update(JsonElement update)
    {
        if (update.TryGetProperty("sessionUpdate", out var commandKind) && commandKind.GetString() == "available_commands_update")
        { chat.Commands = SlashCommand.Read(update); Changed?.Invoke(); return; }
        if (update.TryGetProperty("sessionUpdate", out var configKind) && configKind.GetString() == "config_option_update") { Configure(update); return; }
        if ((loading && !replaying) || lifetime.IsCancellationRequested) return;
        var kind = update.GetProperty("sessionUpdate").GetString();
        if (kind is "agent_message_chunk" or "agent_thought_chunk" or "user_message_chunk")
        {
            var content = update.GetProperty("content");
            if (content.GetProperty("type").GetString() != "text") return;
            var role = kind == "agent_message_chunk" ? "assistant" : kind == "user_message_chunk" ? "user" : "thought";
            var last = chat.Messages.LastOrDefault();
            if (last?.Role != role) { Add(role, ""); last = chat.Messages.Last(); }
            last.Text += content.GetProperty("text").GetString();
        }
        else if (kind is "tool_call" or "tool_call_update")
        {
            var id = update.GetProperty("toolCallId").GetString();
            var message = chat.Messages.LastOrDefault(m => m.ToolId == id);
            if (message is null && id is not null) message = (await store.ReadPageAsync(chat, limit: 1, token: lifetime.Token, toolId: id)).FirstOrDefault();
            if (lifetime.IsCancellationRequested) return;
            if (message is null) { Add("tool", "", id); message = chat.Messages.Last(); }
            if (id is not null && activeToolInputs.TryGetValue(id, out var previousInput)) message.ToolInput = previousInput;
            var title = update.TryGetProperty("title", out var t) ? t.GetString() : message.Text.Split('\n')[0];
            var status = update.TryGetProperty("status", out var s) ? s.GetString() : "running";
            if (update.TryGetProperty("rawInput", out var input) && input.ValueKind is not JsonValueKind.Null)
                message.ToolInput = "\n\n```\n" + (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var commandInput) && commandInput.ValueKind == JsonValueKind.String ? commandInput.GetString() : input.GetRawText()) + "\n```";
            if (id is not null && message.ToolInput.Length > 0) activeToolInputs[id] = message.ToolInput;
            var details = "";
            if (update.TryGetProperty("content", out var contents))
                foreach (var item in contents.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var value)) details += "\n\n" + value.GetString();
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "diff") details += "\n\n```diff\n" + (item.TryGetProperty("oldText", out var old) ? "- " + old.GetString() : "") + "\n+ " + item.GetProperty("newText").GetString() + "\n```";
                }
            message.Text = $"{title}\n\n*{status}*{message.ToolInput}{details}"; if (!replaying) store.SaveMessage(chat, message);
            if (id is not null && status is "completed" or "failed") activeToolInputs.Remove(id);
        }
        else if (kind == "plan") Add("assistant", string.Join("\n", update.GetProperty("entries").EnumerateArray().Select(e => $"- [{(e.GetProperty("status").GetString() == "completed" ? "x" : " ")}] {e.GetProperty("content").GetString()}")));
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    { lifetime.Cancel(); if (client is not null) { await client.DisposeAsync(); client = null; } if (activeTask is not null) await activeTask; if (steeringTask is not null) await steeringTask; if (reconnectTask is not null) await reconnectTask; if (recoveryTask is not null) await recoveryTask; }
}
