using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Markdig;

namespace CodexManager;

// A chat on a paired computer, presented to the desktop chat pane through IChatSession. It polls
// the host's "chat" method while the chat is open and keeps an ordinary Chat object (messages,
// status, config, commands, queue) current, so the pane renders it exactly like a local chat.
// Pending permission and question requests are forwarded to the pane's own cards.
public sealed class RemoteChatSession : IChatSession
{
    private readonly RemoteHostClient host;
    private readonly Chat chat;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, string> revisions = [];
    private readonly Dictionary<string, CancellationTokenSource> requests = [];
    private JsonNode? state;
    private string configJson = "", queueJson = "", commandsJson = "";
    private bool active, activateNext, polling, disposed, canSteer;
    public event Action? Changed;
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? Permission { get; set; }
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? Elicitation { get; set; }
    public RemoteHostClient Host => host;

    public RemoteChatSession(RemoteHostClient host, Chat chat)
    {
        this.host = host; this.chat = chat;
        timer.Tick += async (_, _) => await Poll();
    }

    // Older hosts do not report session state; fall back to what their summaries imply.
    private bool Flag(string name, bool fallback = false) => state?[name]?.GetValue<bool>() ?? fallback;
    public bool IsLoadingHistory => Flag("loadingHistory");
    public bool IsReconnecting => Flag("reconnecting", chat.Status.StartsWith("Reconnecting", StringComparison.Ordinal));
    public bool IsConfiguring => Flag("configuring");
    public bool IsRecovering => Flag("recovering");
    public bool IsPreparing { get; private set; }
    public bool IsPrompting => Flag("prompting", chat.Busy && !IsPreparing);
    public bool IsConnected => host.Connected && Flag("connected", true);
    public bool SupportsSteering => canSteer;
    public bool IsSteering => false;

    // Polls only while the chat is shown; sidebar summaries cover the rest.
    public void Activate(bool userInitiated)
    {
        if (disposed) return;
        active = true; activateNext |= userInitiated;
        timer.Interval = TimeSpan.FromMilliseconds(250); timer.Start();
        _ = Poll();
    }
    public void Deactivate()
    {
        active = false; timer.Stop();
        foreach (var request in requests.Values) request.Cancel();
        requests.Clear();
    }
    // History was rewritten on the host (edit, fork, checkpoint); reload the window.
    public void ResetMessages()
    {
        revisions.Clear(); chat.Messages.Clear();
        if (active) _ = Poll();
    }
    public Task<JsonNode?> Call(JsonObject request)
    {
        request["chatId"] = chat.RemoteId;
        return host.Call(request);
    }
    private async Task<JsonNode> Required(JsonObject request, string failure)
    {
        var result = await Call(request) ?? throw new IOException(host.LastError ?? failure);
        if (result is JsonObject summary && summary["id"] is not null) ApplySummary(summary);
        Changed?.Invoke();
        return result;
    }
    private async Task Poll()
    {
        if (polling || !active || disposed) return;
        polling = true;
        try
        {
            var known = new JsonObject();
            foreach (var message in chat.Messages.TakeLast(Chat.HistoryPageSize)) if (revisions.TryGetValue(message.Id, out var revision)) known[message.Id] = revision;
            var result = await Call(new() { ["method"] = "chat", ["activate"] = activateNext, ["knownMessages"] = known });
            if (result is null || !active || disposed) return;
            activateNext = false;
            ApplySummary(result);
            state = result["session"];
            canSteer = result["canSteer"]?.GetValue<bool>() == true;
            IsPreparing = result["preparing"]?.GetValue<bool>() ?? (chat.Busy && chat.Status is { } status
                && (status.StartsWith("Loading", StringComparison.Ordinal) || status.StartsWith("Connecting", StringComparison.Ordinal) || status.StartsWith("Reconnecting", StringComparison.Ordinal)));
            timer.Interval = TimeSpan.FromMilliseconds(chat.Busy || IsPreparing ? 250 : 2000);
            ApplyCommands(result); ApplyConfig(result); ApplyQueue(result);
            ApplyMessages(result["messages"]!.AsArray());
            ApplyRequests(result);
            chat.HistoryLoaded = true;
            Changed?.Invoke();
        }
        catch (Exception error) { AppDiagnostics.Record("Remote chat refresh", error); }
        finally { polling = false; }
    }
    private void ApplySummary(JsonNode summary) => ApplySummary(chat, summary);
    // Also applied from the host catalog for chats that are not open.
    public static void ApplySummary(Chat chat, JsonNode summary)
    {
        if (summary["title"]?.GetValue<string>() is { } title) chat.Title = title;
        if (summary["status"]?.GetValue<string>() is { } status) chat.Status = status;
        if (summary["busy"] is { } busy) chat.Busy = busy.GetValue<bool>();
        chat.NeedsPermission = summary["needsPermission"]?.GetValue<bool>() == true;
        chat.HasUnreadCompletion = summary["unread"]?.GetValue<bool>() == true;
        if (summary["archived"] is { } archived) chat.Archived = archived.GetValue<bool>();
        if (DateTimeOffset.TryParse(summary["updated"]?.GetValue<string>(), out var updated)) chat.Updated = updated;
        chat.NeedsLogin = summary["session"]?["needsLogin"]?.GetValue<bool>() == true;
        // The host owns the interrupted request; a marker lets the pane offer Resume.
        chat.InterruptedInput = summary["interrupted"]?.GetValue<bool>() == true ? chat.InterruptedInput ?? new PendingInput("", []) : null;
    }
    private void ApplyCommands(JsonNode result)
    {
        var json = (result["commandOptions"] ?? result["commands"])?.ToJsonString() ?? "";
        if (json == commandsJson) return;
        commandsJson = json;
        chat.Commands = result["commandOptions"] is JsonArray detailed
            ? detailed.Select(c => new SlashCommand(c!["name"]!.GetValue<string>(), c["description"]?.GetValue<string>() ?? "", c["hint"]?.GetValue<string>())).ToArray()
            : result["commands"]?.AsArray().Select(c => new SlashCommand(c!.GetValue<string>().TrimStart('/'), "", null)).ToArray() ?? [];
    }
    private void ApplyConfig(JsonNode result)
    {
        var json = result["config"]?.ToJsonString() ?? "[]";
        if (json == configJson) return;
        configJson = json;
        chat.ConfigOptions = result["config"]?.AsArray().Select(c => new SessionConfig(c!["id"]!.GetValue<string>(), c["name"]!.GetValue<string>(), c["kind"]?.GetValue<string>() ?? "select",
            c["current"]!.GetValue<string>(), c["values"]!.AsArray().Select(v => new SessionValue(v!["value"]!.GetValue<string>(), v["name"]!.GetValue<string>())).ToArray())).ToArray() ?? [];
        chat.ConfigVersion++;
    }
    // The host's recently used models for this chat's provider, for the model picker.
    public string[] RecentModels => lastRecentModels;
    private string[] lastRecentModels = [];
    private void ApplyQueue(JsonNode result)
    {
        lastRecentModels = result["recentModels"]?.AsArray().Select(v => v!.GetValue<string>()).ToArray() ?? [];
        var json = result["queue"]?.ToJsonString() ?? "[]";
        if (json == queueJson) return;
        queueJson = json;
        chat.QueuedInputs.Clear();
        // The host keeps queued attachments; placeholders only carry their count to the pane.
        foreach (var item in result["queue"]?.AsArray() ?? [])
            chat.QueuedInputs.Add(new PendingInput(item!["text"]?.GetValue<string>() ?? "",
                Enumerable.Range(0, item["attachments"]?.GetValue<int>() ?? 0).Select(i => new Attachment("Attachment " + (i + 1), "application/octet-stream", "")).ToArray())
            { Id = item["id"]!.GetValue<string>() });
    }
    private Message ReadMessage(JsonNode row)
    {
        var message = new Message
        {
            Timestamp = DateTimeOffset.TryParse(row["timestamp"]?.GetValue<string>(), out var timestamp) ? timestamp : null,
            Provider = chat.Provider, Id = row["id"]!.GetValue<string>(), Role = row["role"]!.GetValue<string>(),
            Sequence = row["sequence"]?.GetValue<int>() ?? 0, Text = row["text"]?.GetValue<string>() ?? ""
        };
        message.Subagent = row["subagent"]?.Deserialize(StoreJsonContext.Default.SubagentInfo);
        message.AsyncTask = AsyncTaskInfo.FromJson(row["asyncTask"]);
        foreach (var file in row["attachments"]?.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? []) message.Attachments.Add(file);
        return message;
    }
    private void ApplyMessages(JsonArray rows)
    {
        Dictionary<string, Message>? byId = null;
        foreach (var row in rows)
        {
            var id = row!["id"]!.GetValue<string>();
            if (row["revision"] is { } revision) revisions[id] = revision.GetValue<string>();
            if (row["text"] is null) continue;
            byId ??= chat.Messages.ToDictionary(m => m.Id);
            if (!byId.TryGetValue(id, out var message))
            {
                message = ReadMessage(row);
                var index = chat.Messages.Count;
                while (index > 0 && chat.Messages[index - 1].Sequence > message.Sequence) index--;
                chat.Messages.Insert(index, message); byId.Add(id, message);
                continue;
            }
            message.Text = row["text"]!.GetValue<string>();
            message.Subagent = row["subagent"]?.Deserialize(StoreJsonContext.Default.SubagentInfo);
            message.AsyncTask = AsyncTaskInfo.FromJson(row["asyncTask"]);
            if (row["attachments"] is { } files)
            {
                var incoming = files.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? [];
                if (!message.Attachments.SequenceEqual(incoming)) { message.Attachments.Clear(); foreach (var file in incoming) message.Attachments.Add(file); }
            }
        }
        var keep = HistoryWindow.Last(chat.Messages, Chat.HistoryPageSize).Length;
        while (chat.Messages.Count > keep) chat.Messages.RemoveAt(0);
        var retained = chat.Messages.Select(m => m.Id).ToHashSet();
        foreach (var id in revisions.Keys.Where(id => !retained.Contains(id)).ToArray()) revisions.Remove(id);
        if (chat.Messages.Count > 0) chat.NextSequence = Math.Max(chat.NextSequence, chat.Messages[^1].Sequence + 1);
    }
    private void ApplyRequests(JsonNode result)
    {
        var current = new HashSet<string>();
        foreach (var permission in result["permissions"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = permission["id"]!.GetValue<string>(); current.Add(id);
            if (requests.ContainsKey(id) || Permission is not { } ask) continue;
            var cancellation = requests[id] = new CancellationTokenSource();
            _ = Answer(id, permission, cancellation.Token, async request =>
            {
                var answer = await ask(request, cancellation.Token);
                if (answer["outcome"]?["optionId"]?.GetValue<string>() is not { } option) return true;
                return await Call(new() { ["method"] = "approve", ["permissionId"] = id, ["optionId"] = option }) is not null;
            });
        }
        foreach (var question in result["elicitations"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = question["id"]!.GetValue<string>(); current.Add(id);
            if (requests.ContainsKey(id) || Elicitation is not { } ask) continue;
            var cancellation = requests[id] = new CancellationTokenSource();
            _ = Answer(id, question, cancellation.Token, async request =>
            {
                var answer = await ask(request, cancellation.Token);
                return await Call(new() { ["method"] = "elicitation/respond", ["elicitationId"] = id, ["response"] = answer }) is not null;
            });
        }
        foreach (var resolved in requests.Keys.Except(current).ToArray()) { requests[resolved].Cancel(); requests.Remove(resolved); }
    }
    private async Task Answer(string id, JsonObject request, CancellationToken token, Func<JsonElement, Task<bool>> respond)
    {
        try
        {
            using var parsed = JsonDocument.Parse(request.ToJsonString());
            // A failed reply is shown again on the next poll so it can be retried.
            if (!await respond(parsed.RootElement.Clone()) && !token.IsCancellationRequested) requests.Remove(id);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Remote chat request", error); if (!token.IsCancellationRequested) requests.Remove(id); }
    }

    public async Task Send(string text, Attachment[] attachments, bool autoResume = false)
    {
        activateNext = false;
        try { await Required(new() { ["method"] = "send", ["text"] = text, ["attachments"] = JsonSerializer.SerializeToNode(attachments, StoreJsonContext.Default.AttachmentArray) }, "Could not send. Reconnect and try again."); }
        catch (IOException error) { Recover(new(text, attachments), error.Message); }
        if (active) _ = Poll();
    }
    // The pane clears its composer before sending; put an undelivered message back.
    private void Recover(PendingInput input, string reason)
    {
        chat.RecoverInput(input); chat.Status = "Not sent: " + reason; Changed?.Invoke();
    }
    public void Queue(PendingInput input) => _ = QueueCore(input);
    private async Task QueueCore(PendingInput input)
    {
        try { await Required(new() { ["method"] = "queue", ["text"] = input.Text, ["attachments"] = JsonSerializer.SerializeToNode(input.Attachments, StoreJsonContext.Default.AttachmentArray) }, "Could not queue the message."); }
        catch (IOException error) { Recover(input, error.Message); }
        if (active) _ = Poll();
    }
    public void RemoveQueued(PendingInput input) => _ = Try(new() { ["method"] = "queue/remove", ["queueId"] = input.Id });
    public async Task EditQueued(PendingInput input, string text) => await Try(new() { ["method"] = "queue/edit", ["queueId"] = input.Id, ["text"] = text });
    public async Task<bool> Steer(PendingInput input)
    {
        if (chat.QueuedInputs.Any(q => q.Id == input.Id))
            return await Call(new() { ["method"] = "queue/steer", ["queueId"] = input.Id }) is not null;
        var result = await Call(new() { ["method"] = "steer", ["text"] = input.Text, ["attachments"] = JsonSerializer.SerializeToNode(input.Attachments, StoreJsonContext.Default.AttachmentArray) });
        return result?.GetValueKind() == JsonValueKind.True;
    }
    public Task SendQueuedNow(PendingInput input, bool waitForCompletion = true) => Try(new() { ["method"] = "queue/send", ["queueId"] = input.Id });
    public Task AdvanceQueued(bool interrupt = false) => Try(new() { ["method"] = interrupt ? "queue/interrupt" : "queue/advance" });
    public Task Stop() => Try(new() { ["method"] = "stop" });
    public Task StopAsyncTask(string taskId) => Try(new() { ["method"] = "task/stop", ["taskId"] = taskId });
    public Task SetConfig(SessionConfig config, string value) => Try(new() { ["method"] = "config", ["configId"] = config.Id, ["value"] = value });
    public Task Reconnect(bool automatic = false) => Try(new() { ["method"] = "reconnect" });
    public Task LoadHistory() { activateNext = true; return Poll(); }
    public Task Resume() => Try(new() { ["method"] = "resume" });
    public Task MarkRead() => Try(new() { ["method"] = "read" });
    private async Task Try(JsonObject request)
    {
        try { await Required(request, "The host did not respond."); }
        catch (IOException error) { chat.Status = error.Message; Changed?.Invoke(); }
        if (active) _ = Poll();
    }

    public async Task<Message[]> ReadPage(int sequence, bool newer)
    {
        var result = await Call(new() { ["method"] = "chat", ["activate"] = false, [newer ? "after" : "before"] = sequence })
            ?? throw new IOException(host.LastError ?? "Could not load history. Reconnect and try again.");
        return result["messages"]!.AsArray().Where(r => r?["text"] is not null).Select(r => ReadMessage(r!)).ToArray();
    }
    // Hosts from before outlines simply return none; the transcript then spans what is loaded.
    public async Task<TranscriptOutline?> ReadOutline()
    {
        try { return TranscriptOutline.FromJson(chat.Id, await Call(new() { ["method"] = "chat/outline" })); }
        catch (Exception error) when (error is IOException or RemoteOperationException) { return null; }
    }
    public async Task<ChatSearchHit[]> Search(string query)
    {
        var result = await Call(new() { ["method"] = "chat/search", ["query"] = query }) ?? throw new IOException(host.LastError ?? "Search is unavailable. Reconnect and try again.");
        return result.AsArray().Select(h => new ChatSearchHit(h!["id"]!.GetValue<string>(), h["sequence"]!.GetValue<int>(), h["preview"]?.GetValue<string>() ?? "")).ToArray();
    }
    public async Task<(string Plain, string Html)> Export()
    {
        var plain = new System.Text.StringBuilder(); var html = new System.Text.StringBuilder();
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var after = -1;
        while (true)
        {
            var page = await Call(new() { ["method"] = "chat/export", ["after"] = after }) ?? throw new IOException("Could not retrieve the complete chat. Reconnect and try again.");
            foreach (var item in page["messages"]!.AsArray())
            {
                var label = item!["label"]!.GetValue<string>(); var text = item["text"]!.GetValue<string>();
                plain.Append(label).Append('\n').Append(text).Append("\n\n");
                html.Append("<h3>").Append(System.Net.WebUtility.HtmlEncode(label)).Append("</h3>").Append(Markdig.Markdown.ToHtml(text, pipeline));
            }
            if (page["after"] is null) break;
            var next = page["after"]!.GetValue<int>();
            if (next <= after) throw new IOException("The host returned a repeated chat page.");
            after = next;
        }
        return (plain.ToString(), html.ToString());
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true; Deactivate(); Changed = null; Permission = null; Elicitation = null;
        return ValueTask.CompletedTask;
    }
}
