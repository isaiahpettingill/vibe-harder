using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed partial class ChatRuntime
{
    public bool IsChangingHistory { get; private set; }
    public bool SupportsFork { get; private set; }
    private bool supportsClaudePoint;
    private Task<Chat>? historyTask;
    private async Task WaitForHistoryShutdown()
    {
        try { if (historyTask is not null) await historyTask; }
        catch (Exception error) { AppDiagnostics.Record("History operation ended during shutdown", error); }
    }
    private void ReadHistoryCapabilities(JsonElement init)
    {
        SupportsFork = init.TryGetProperty("agentCapabilities", out var caps) && caps.TryGetProperty("sessionCapabilities", out var sessions) &&
            sessions.TryGetProperty("fork", out var fork) && fork.ValueKind == JsonValueKind.Object;
        // This extension is released in the pinned Claude adapter, but is not an ACP standard.
        supportsClaudePoint = SupportsFork && init.TryGetProperty("agentInfo", out var info) && info.TryGetProperty("name", out var name) &&
            name.GetString() == "@agentclientprotocol/claude-agent-acp" && info.TryGetProperty("version", out var version) &&
            Version.TryParse(version.GetString()?.Split('-')[0], out var parsed) && parsed >= new Version(0, 76, 0);
    }
    private static string Fingerprint(string text) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private async Task<Message?> HistoryMessage(string? id, int sequence)
    {
        if (id is null) return null;
        if (sequence < 0 || sequence == int.MaxValue) throw new IOException("This message has no saved history position yet.");
        foreach (var message in chat.Messages) store.SaveMessage(chat, message);
        return (await store.ReadPageAsync(chat, sequence + 1, 1, lifetime.Token)).SingleOrDefault(m => m.Id == id)
            ?? throw new IOException("The chat history has changed. Select the message again.");
    }
    private async Task<Message?> PreviousHistoryMessage(Message selected)
    {
        var before = selected.Sequence;
        while (before > 0)
        {
            var page = await store.ReadPageAsync(chat, before, 50, lifetime.Token);
            if (page.LastOrDefault(m => m.Role is "user" or "assistant" or "tool") is { } previous) return previous;
            if (page.Length == 0) break;
            before = page[0].Sequence;
        }
        return null;
    }
    private async Task<bool> CanBranchAt(Message point, bool stopped = false)
    {
        if (!supportsClaudePoint || point.Role is not ("assistant" or "tool") || point.Role == "tool" && point.ProviderMessageId is null) return false;
        // A protocol message may span multiple visible rows. Never present an earlier segment as an exact boundary.
        if (point.ProviderMessageId is { } id)
        {
            var next = await store.ReadPageAsync(chat, point.Sequence, 50, lifetime.Token, newer: true);
            if (next.Any(m => m.ProviderMessageId == id)) return false;
        }
        return stopped || !chat.Busy || chat.Messages.LastOrDefault()?.Id != point.Id;
    }
    public async Task<JsonObject> HistoryOptions(string? id, int sequence)
    {
        if (IsChangingHistory || IsLoadingHistory || IsReconnecting || IsConfiguring || IsRecovering) throw new IOException("Wait for the current session operation to finish.");
        await Connect();
        var selected = await HistoryMessage(id, sequence);
        var previous = selected?.Role == "user" ? await PreviousHistoryMessage(selected) : null;
        var edit = selected?.Role == "user" && (previous is null || await CanBranchAt(previous));
        var point = selected is not null && await CanBranchAt(selected);
        return new JsonObject
        {
            ["fork"] = SupportsFork, ["point"] = point, ["edit"] = edit,
            ["checkpoint"] = selected is null ? null : Fingerprint(selected.Text),
            ["reason"] = !SupportsFork ? "This ACP adapter does not support session forking or history rollback."
                : !supportsClaudePoint ? "This ACP adapter supports full-chat forks, but not forks or edits at a specific message."
                : "This location is not a completed, independently addressable agent message. Grouped output and tool calls can only be forked at boundaries exposed by the adapter."
        };
    }
    public Task<Chat> BranchHistory(string? id, int sequence, string? checkpoint, bool fork) =>
        IsChangingHistory ? Task.FromException<Chat>(new IOException("A history change is already in progress.")) : historyTask = BranchHistoryCore(id, sequence, checkpoint, fork);
    private async Task<Chat> BranchHistoryCore(string? id, int sequence, string? checkpoint, bool fork)
    {
        if (IsLoadingHistory || IsReconnecting || IsConfiguring || IsSteering || IsRecovering) throw new IOException("Wait for the current session operation to finish.");
        IsChangingHistory = true; Changed?.Invoke();
        Chat? candidate = null;
        var committed = false;
        try
        {
            var pending = activeTask;
            if (IsPrompting) { await Stop(); if (pending is not null) await pending; }
            if (chat.Busy) throw new IOException("Stop the running chat before changing its history.");
            chat.Busy = true;
            await Connect();
            var selected = await HistoryMessage(id, sequence);
            if (selected is not null && Fingerprint(selected.Text) != checkpoint) throw new IOException("This message changed while the action was open. Select it again.");
            var point = selected?.Role == "user" ? await PreviousHistoryMessage(selected) : selected;
            var empty = selected?.Role == "user" && point is null;
            if (!empty && (!SupportsFork || point is not null && !await CanBranchAt(point, true))) throw new IOException("The adapter cannot fork at this history boundary.");
            candidate = new Chat { WorkspaceId = chat.WorkspaceId, Provider = chat.Provider, Title = chat.Title + " (fork)", RetainHistory = true };
            if (!empty)
            {
                var parameters = RpcJson.Object(("sessionId", chat.SessionId), ("cwd", workspace.Path), ("mcpServers", new JsonArray()));
                if (point is not null)
                {
                    var occurrence = 0; int? cursor = null;
                    do
                    {
                        var page = await store.ReadPageAsync(chat, cursor, 50, lifetime.Token, newer: true);
                        foreach (var message in page.Where(m => m.Sequence <= point.Sequence && m.Role == "assistant" && m.Text == point.Text)) occurrence++;
                        if (page.Length == 0 || page[^1].Sequence >= point.Sequence) break;
                        cursor = page[^1].Sequence;
                    } while (true);
                    parameters["_meta"] = new JsonObject { ["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["fork"] = new JsonObject
                    {
                        ["version"] = 1, ["messageId"] = point.ProviderMessageId ?? point.Id,
                        ["messageFingerprint"] = Fingerprint(point.Text), ["messageOccurrence"] = Math.Max(1, occurrence)
                    } } } };
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                JsonElement response;
                loading = true;
                try { response = await client!.Request("session/fork", parameters, timeout.Token); }
                finally { loading = false; }
                candidate.SessionId = response.GetProperty("sessionId").GetString();
                if (string.IsNullOrWhiteSpace(candidate.SessionId) || candidate.SessionId == chat.SessionId) throw new IOException("The adapter did not create a separate session.");
            }
            foreach (var option in chat.ConfigOptions.Where(option => FullAccess(option) is not null))
                store.Setting("sessionAccess:" + candidate.Id + ":" + option.Id, option.Current);
            store.Save(candidate);
            await using (var branch = new ChatRuntime(candidate, workspace, store, command))
            {
                if (empty) { await branch.Connect().WaitAsync(lifetime.Token); candidate.HistoryLoaded = true; }
                else
                {
                    await branch.LoadHistory().WaitAsync(lifetime.Token);
                    if (!candidate.HistoryLoaded || !branch.IsConnected) throw new IOException(candidate.Status);
                    if (point is not null)
                    {
                        var end = candidate.Messages.LastOrDefault(m => m.Role is "assistant" or "tool" or "user");
                        if (end is null || end.Role != point.Role || end.Text != point.Text || point.ProviderMessageId is not null && end.ProviderMessageId != point.ProviderMessageId)
                            throw new IOException("The adapter returned a different history boundary. The original chat was kept unchanged.");
                    }
                }
                foreach (var option in chat.ConfigOptions.OrderByDescending(ModelPicker.IsModel))
                {
                    var inherited = candidate.ConfigOptions.FirstOrDefault(value => value.Id == option.Id);
                    if (inherited is null || inherited.Current == option.Current || !inherited.Values.Any(value => value.Value == option.Current)) continue;
                    await branch.SetConfig(inherited, option.Current).WaitAsync(lifetime.Token);
                    if (candidate.ConfigOptions.FirstOrDefault(value => value.Id == option.Id)?.Current != option.Current) throw new IOException("Could not preserve the chat's " + option.Name + " setting.");
                }
                lifetime.Token.ThrowIfCancellationRequested();
            }
            if (fork) { store.Save(candidate); await store.FlushAsync(); committed = true; return candidate; }
            connected = false;
            if (client is not null) { await client.DisposeAsync(); client = null; }
            await store.AdoptBranch(chat, candidate); committed = true;
            foreach (var option in chat.ConfigOptions.Where(option => FullAccess(option) is not null)) store.Setting(AccessKey(option), option.Current);
            chat.PendingInput = chat.InterruptedInput = null; chat.QueuedInputs.Clear(); chat.NeedsPermission = false;
            store.Setting("interrupted:" + chat.Id, ""); store.Setting("historyIncomplete:" + chat.Id, "0");
            chat.Status = "Ready"; store.Save(chat); await store.FlushAsync(); return chat;
        }
        finally
        {
            try { if (!committed && candidate is not null) store.Delete(candidate); }
            catch (Exception error) { AppDiagnostics.Record("Clean up failed chat fork", error); }
            chat.Busy = false; IsChangingHistory = false;
            try { store.Save(chat); await store.FlushAsync(); }
            catch (Exception error) { AppDiagnostics.Record("Save chat after history change", error); }
            Changed?.Invoke();
        }
    }
}
