using System.Text.RegularExpressions;

namespace CodexManager;

// npx upgrades a cached @latest install in place when a new version is published, and npm can
// drop platform-specific optional dependencies doing so (Codex's native binary), breaking every
// launch until the cache is cleared. Default launchers therefore run exact versions: each set
// installs fresh into its own npx directory, and BackendUpdates switches to a newer set only
// after it has installed and run.
public static partial class BundledPackages
{
    public static string[] For(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => ["@agentclientprotocol/codex-acp", "@openai/codex"],
        AgentProvider.Claude => ["@agentclientprotocol/claude-agent-acp"],
        AgentProvider.Dirac => ["dirac-cli"],
        AgentProvider.Pi => ["pi-acp"],
        _ => []
    };
    private static string Key(Workspace workspace, string package) => $"bundledVersion:{workspace.Distro ?? "local"}:{package}";
    public static string? Pinned(Store store, Workspace workspace, string package) => store.Setting(Key(workspace, package)) is { Length: > 0 } version ? version : null;
    public static void Pin(Store store, Workspace workspace, IReadOnlyDictionary<string, string> versions)
    {
        foreach (var (package, version) in versions) store.Setting(Key(workspace, package), version);
    }
    // Only the shipped launcher is rewritten; custom commands run exactly as entered.
    public static string Apply(Store store, Workspace workspace, AgentProvider provider, string command)
    {
        if (command != AgentProviders.Get(provider).DefaultCommand) return command;
        var packages = For(provider);
        foreach (var package in packages)
            if (Pinned(store, workspace, package) is { } version) command = command.Replace(package + "@latest", package + "@" + version, StringComparison.Ordinal);
        // A fully pinned set is already installed; launching must not wait on the registry.
        if (packages.Length > 0 && packages.All(package => Pinned(store, workspace, package) is not null) && command.StartsWith("npx -y ", StringComparison.Ordinal))
            command = "npx -y --prefer-offline " + command["npx -y ".Length..];
        return command;
    }
    // Installs the exact set (a new npx directory) and runs it; Codex must start its binary.
    public static string Install(AgentProvider provider, IReadOnlyDictionary<string, string> versions) =>
        "npx -y " + string.Join(" ", For(provider).Select(package => "--package=" + package + "@" + versions[package])) +
        " -- " + (provider == AgentProvider.Codex ? "codex --version" : "node -e 0");
    public static string Lookup(string package) => "npm view " + package + " version";
    public static string ParseVersion(string output) => output.Trim() is var version && VersionPattern().IsMatch(version)
        ? version : throw new IOException("npm returned an unexpected version: " + output.Trim());
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();
}
