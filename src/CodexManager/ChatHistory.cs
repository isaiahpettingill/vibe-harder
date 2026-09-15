using System.Text.Json.Nodes;
using System.Text.Json;

namespace CodexManager;

public static class ChatHistory
{
    public const string DefaultCodexCommand = "npx -y @openai/codex@0.154.0";
    public static async Task<List<Chat>> Discover(Workspace workspace, string command, CancellationToken token = default, AgentProvider provider = AgentProvider.Codex, Action<bool>? authenticationChanged = null)
    {
        await using var client = new AcpClient(AgentProviders.Start(workspace, command, provider));
        client.AuthenticationChanged += needsLogin => authenticationChanged?.Invoke(needsLogin);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var init = await client.Initialize(timeout.Token);
        if (!init.TryGetProperty("agentCapabilities", out var capabilities) ||
            !capabilities.TryGetProperty("sessionCapabilities", out var sessions) ||
            !sessions.TryGetProperty("list", out _))
            throw new IOException("The configured adapter does not support chat history discovery.");
        List<Chat> result = [];
        HashSet<string> ids = [];
        HashSet<string> cursors = [];
        string? cursor = null;
        do
        {
            var page = await client.Request("session/list", RpcJson.Object(("cwd", workspace.Path), ("cursor", cursor)), timeout.Token);
            foreach (var item in page.GetProperty("sessions").EnumerateArray())
            {
                var id = item.GetProperty("sessionId").GetString();
                if (string.IsNullOrEmpty(id) || !SameFolder(workspace, item.GetProperty("cwd").GetString() ?? "") || !ids.Add(id)) continue;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                result.Add(new Chat
                {
                    WorkspaceId = workspace.Id,
                    SessionId = id,
                    Provider = provider,
                    Title = string.IsNullOrWhiteSpace(title) ? $"Previous {AgentProviders.Get(provider).Name} chat" : title,
                    Updated = item.TryGetProperty("updatedAt", out var updated) && DateTimeOffset.TryParse(updated.GetString(), out var date) ? date : DateTimeOffset.MinValue
                });
            }
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (cursor is not null && !cursors.Add(cursor)) throw new IOException("The adapter repeated a history cursor.");
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }
    private static bool SameFolder(Workspace workspace, string candidate)
    {
        if (workspace.IsWsl || !OperatingSystem.IsWindows())
            return workspace.Path.TrimEnd('/') == candidate.TrimEnd('/');
        return string.Equals(workspace.Path.Replace('/', '\\').TrimEnd('\\'), candidate.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
    public static async Task DeleteFromCodex(Workspace workspace, string sessionId, string? codexCommand = null)
    {
        if (!Guid.TryParse(sessionId, out var id)) throw new ArgumentException("Permanent deletion requires a valid Codex session UUID.");
        // ACP session/delete currently maps to thread/archive. Codex CLI delete performs a real
        // history deletion through its supported thread store, including spawned descendants.
        var command = (codexCommand ?? DefaultCodexCommand) + " delete --force " + id.ToString("D");
        await Hosts.Capture(Hosts.Agent(workspace, command), TimeSpan.FromSeconds(90));
    }
    public static async Task<string?> TryDeleteFromProvider(Store store, Workspace workspace, Chat chat)
    {
        if (chat.SessionId is not { } id) return null;
        try
        {
            if (chat.Provider == AgentProvider.Codex)
                await DeleteFromCodex(workspace, id, store.Setting(workspace.IsWsl ? "wslCodexCommand" : "localCodexCommand"));
            else
            {
                var command = AgentProviders.Command(store, workspace, chat.Provider);
                if (chat.Provider == AgentProvider.OpenCode && command.TrimEnd().EndsWith(" acp", StringComparison.Ordinal))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\Ases_[a-zA-Z0-9]+\z")) throw new IOException("Invalid OpenCode session ID.");
                    await Hosts.Capture(Hosts.Agent(workspace, command.TrimEnd()[..^4] + " session delete " + id), TimeSpan.FromSeconds(90));
                }
                else
                {
                    await using var client = new AcpClient(AgentProviders.Start(workspace, command, chat.Provider));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var init = await client.Initialize(timeout.Token);
                    if (!init.TryGetProperty("agentCapabilities", out var capabilities) || !capabilities.TryGetProperty("sessionCapabilities", out var sessions) ||
                        !sessions.TryGetProperty("delete", out var deletion) || deletion.ValueKind is JsonValueKind.False or JsonValueKind.Null)
                        return AgentProviders.Get(chat.Provider).Name + " does not expose history deletion through ACP.";
                    await client.Request("session/delete", RpcJson.Object(("sessionId", id)), timeout.Token);
                }
            }
            return null;
        }
        catch (Exception error) { AppDiagnostics.Record("Provider history deletion", error); return AgentProviders.Get(chat.Provider).Name + ": " + error.Message; }
    }
}
