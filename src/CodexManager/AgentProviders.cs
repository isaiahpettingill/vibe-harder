namespace CodexManager;

public enum AgentProvider { Codex, Claude, OpenCode, VTCode, Dirac, Pi }

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
        new(AgentProvider.OpenCode, "OpenCode", "▣", "opencode acp"),
        new(AgentProvider.VTCode, "VT Code", "◇", "vtcode acp"),
        new(AgentProvider.Dirac, "Dirac", "◇", "npx -y dirac-cli@0.5.13 --acp"),
        new(AgentProvider.Pi, "Pi", "◇", "npx -y pi-acp@0.0.33")
    ];
    public static bool IsAdditional(AgentProvider provider) => provider is AgentProvider.VTCode or AgentProvider.Dirac or AgentProvider.Pi;
    public static bool IsEnabled(Store store, AgentProvider provider) => Enum.IsDefined(provider) && (!IsAdditional(provider) || store.Setting("additionalAgentsEnabled") == "1");
    public static IEnumerable<AgentOption> Enabled(Store store) => All.Where(p => IsEnabled(store, p.Provider));
    public static AgentOption Get(AgentProvider provider) => All.Single(p => p.Provider == provider);
    public static string CommandKey(AgentProvider provider, bool wsl) =>
        provider == AgentProvider.Codex ? (wsl ? "wslCommand" : "localCommand") : $"{provider}:{(wsl ? "wsl" : "local")}Command";
    public static string Command(Store store, Workspace workspace, AgentProvider provider) =>
        store.Setting(CommandKey(provider, workspace.IsWsl)) ?? Get(provider).DefaultCommand;
    public static System.Diagnostics.ProcessStartInfo Start(Workspace workspace, string command, AgentProvider provider) =>
        Hosts.Agent(workspace, command, provider == AgentProvider.VTCode ? new Dictionary<string, string> { ["VT_ACP_ENABLED"] = "1", ["VT_ACP_ZED_ENABLED"] = "1" } : null);
    public static string HiddenHistoryKey(Chat chat) => $"hiddenHistory:{chat.WorkspaceId}:{chat.Provider}:{chat.SessionId}";
    public static string LoginCommand(Store store, Workspace workspace, AgentProvider provider) =>
        store.Setting($"{provider}:{(workspace.IsWsl ? "wsl" : "local")}LoginCommand") ?? (provider switch
        {
            AgentProvider.Codex => (store.Setting(workspace.IsWsl ? "wslCodexCommand" : "localCodexCommand") ?? ChatHistory.DefaultCodexCommand) + " login",
            AgentProvider.Claude => "npx -y @anthropic-ai/claude-code@2.1.268 auth login",
            AgentProvider.OpenCode => "opencode auth login",
            AgentProvider.VTCode => "vtcode",
            AgentProvider.Dirac => "npx -y dirac-cli@0.5.13 auth",
            AgentProvider.Pi => "pi",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        });
    public static bool IsAuthenticationError(Exception error) => new[] { "not logged in", "authentication required", "unauthenticated", "login required", "unauthorized", "api key", "auth login", "codex login" }
        .Any(text => error.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
}
