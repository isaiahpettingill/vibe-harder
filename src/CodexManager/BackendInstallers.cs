namespace CodexManager;

public static class BackendInstallers
{
    public sealed record Installer(string? Probe, string Command);
    public static Installer[] For(AgentProvider provider, bool windows) => provider switch
    {
        AgentProvider.Codex => [new(null, windows ? "irm https://chatgpt.com/codex/install.ps1 | iex" : "curl -fsSL https://chatgpt.com/codex/install.sh | sh")],
        AgentProvider.Claude => [new(null, windows ? "irm https://claude.ai/install.ps1 | iex" : "curl -fsSL https://claude.ai/install.sh | bash")],
        AgentProvider.VTCode => [new(null, windows ? "irm https://raw.githubusercontent.com/vinhnx/vtcode/main/scripts/install.ps1 | iex" : "curl -fsSL https://raw.githubusercontent.com/vinhnx/vtcode/main/scripts/install.sh | bash")],
        AgentProvider.OpenCode => [
            // Windows' legacy bash.exe can be a WSL launcher. A local install
            // must use a Windows bash (Git Bash/MSYS), not another environment.
            new(windows ? "bash -lc 'case $(uname -s) in MINGW*|MSYS*|CYGWIN*) exit 0;; *) exit 1;; esac'" : "bash --version",
                "bash -lc 'curl -fsSL https://opencode.ai/v2/install | bash'"),
            new("npm --version", "npm install -g @opencode/cli@latest"),
            new("bun --version", "bun install -g --trust @opencode/cli@latest"),
            new("brew --version", "brew install anomalyco/tap/opencode-v2")],
        AgentProvider.Dirac => [new("npm --version", "npm install -g dirac-cli@latest")],
        AgentProvider.Pi => [new("npm --version", "npm install -g --ignore-scripts @earendil-works/pi-coding-agent@latest")],
        AgentProvider.Cline => [new("npm --version", "npm install -g cline@latest")],
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
    public static async Task Install(Workspace workspace, AgentProvider provider, Func<Workspace, string, CancellationToken, Task<int>> run, CancellationToken token)
    {
        foreach (var installer in For(provider, OperatingSystem.IsWindows() && !workspace.IsWsl))
        {
            token.ThrowIfCancellationRequested();
            if (installer.Probe is not null && await run(workspace, installer.Probe, token) != 0) continue;
            if (await run(workspace, installer.Command, token) == 0 && await run(workspace, BackendUpdates.Plan(provider).Executable + " --version", token) == 0) return;
        }
        throw new IOException("Could not install " + AgentProviders.Get(provider).Name + " in " + workspace.Host + ". Check installer prerequisites, network access, and write permissions.");
    }
}
