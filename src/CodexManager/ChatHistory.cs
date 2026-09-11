using System.Text.Json.Nodes;
using System.Text.Json;

namespace CodexManager;

public static class ChatHistory
{
    public const string DefaultCodexCommand = "npx -y @openai/codex@0.154.0";
    public static async Task<List<Chat>> Discover(Workspace workspace, string command, CancellationToken token = default, AgentProvider provider = AgentProvider.Codex, Action<bool>? authenticationChanged = null)
    {
        await using var client = new AcpClient(Hosts.Agent(workspace, command));
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
}
