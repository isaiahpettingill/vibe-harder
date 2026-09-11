namespace CodexManager;

public enum AgentProvider { Codex, Claude, OpenCode }

public sealed record AgentOption(AgentProvider Provider, string Name, string Icon, string DefaultCommand)
{
    public string Label => Name;
    public override string ToString() => Label;
}

public static class AgentProviders
{
    public static IReadOnlyList<AgentOption> All { get; } = [
        new(AgentProvider.Codex, "Codex", "◇", Hosts.DefaultAdapter),
        new(AgentProvider.Claude, "Claude", "✳", "npx -y @agentclientprotocol/claude-agent-acp@0.76.0"),
        new(AgentProvider.OpenCode, "OpenCode", "▣", "npx -y opencode-ai@1.18.30 acp")
    ];
    public static AgentOption Get(AgentProvider provider) => All.Single(p => p.Provider == provider);
    public static string CommandKey(AgentProvider provider, bool wsl) =>
        provider == AgentProvider.Codex ? (wsl ? "wslCommand" : "localCommand") : $"{provider}:{(wsl ? "wsl" : "local")}Command";
    public static string Command(Store store, Workspace workspace, AgentProvider provider) =>
        store.Setting(CommandKey(provider, workspace.IsWsl)) ?? Get(provider).DefaultCommand;
    public static string HiddenHistoryKey(Chat chat) => $"hiddenHistory:{chat.WorkspaceId}:{chat.Provider}:{chat.SessionId}";
    public static string LoginCommand(Store store, Workspace workspace, AgentProvider provider) =>
        store.Setting($"{provider}:{(workspace.IsWsl ? "wsl" : "local")}LoginCommand") ?? (provider switch
        {
            AgentProvider.Codex => (store.Setting(workspace.IsWsl ? "wslCodexCommand" : "localCodexCommand") ?? ChatHistory.DefaultCodexCommand) + " login",
            AgentProvider.Claude => "npx -y @anthropic-ai/claude-code@2.1.268 auth login",
            AgentProvider.OpenCode => "npx -y opencode-ai@1.18.30 auth login",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        });
    public static bool IsAuthenticationError(Exception error) => new[] { "not logged in", "authentication required", "unauthenticated", "login required", "unauthorized", "api key", "auth login", "codex login" }
        .Any(text => error.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
}
