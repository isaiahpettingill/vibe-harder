namespace CodexManager.Tests;

public class ConfigurationTests
{
    [Fact]
    public void WindowsNpxUsesCmdWithoutChangingExecutionPolicyOrWslCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        var local = new Workspace("w", "Local", Path.GetTempPath());
        var command = "npx -y example login";
        Assert.Equal("npx.cmd -y example login", Hosts.Agent(local, command).ArgumentList.Last());
        Assert.Equal("npx.cmd -y example login", TerminalSession.Options(local, command).CommandLine.Last());
        Assert.Equal("custom-wrapper npx login", Hosts.Agent(local, "custom-wrapper npx login").ArgumentList.Last());
        var wsl = new Workspace("w", "Linux", "/tmp", "Debian");
        Assert.Equal("exec " + command, Hosts.Agent(wsl, command).ArgumentList.Last());
    }
    [Fact]
    public void WslResolutionUsesLinuxOverridesAndKeepsDefaultOpenCodeLayer()
    {
        var files = ConfigurationFiles.Candidates(new Dictionary<string, string?>
        {
            ["HOME"] = "/home/test",
            ["USERPROFILE"] = "C:\\Users\\wrong",
            ["CODEX_HOME"] = "/settings/codex",
            ["CLAUDE_CONFIG_DIR"] = "/settings/claude",
            ["XDG_CONFIG_HOME"] = "/xdg",
            ["OPENCODE_CONFIG_DIR"] = "/custom/opencode",
            ["OPENCODE_CONFIG"] = "config/custom.jsonc",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1"
        }, "Debian", "/repo");
        Assert.Contains(files, f => f.Path == "/settings/codex/AGENTS.override.md");
        Assert.Contains(files, f => f.Path == "/settings/claude/settings.json");
        Assert.Contains(files, f => f.Path == "/xdg/opencode/opencode.json");
        Assert.Contains(files, f => f.Path == "/custom/opencode/opencode.jsonc");
        Assert.Contains(files, f => f.Path == "/repo/config/custom.jsonc");
        Assert.DoesNotContain(files, f => f.Title.Contains("fallback"));
        var start = ConfigurationFiles.EditorStartInfo(files[0]);
        Assert.True(start.UseShellExecute); Assert.Equal("open", start.Verb);
        Assert.StartsWith(@"\\wsl.localhost\Debian\settings\codex\", start.FileName);
        Assert.Empty(start.ArgumentList);
    }
    [Fact]
    public void RelativeXdgIsIgnoredAndHomeFallbacksAreUsed()
    {
        var files = ConfigurationFiles.Candidates(new Dictionary<string, string?> { ["HOME"] = "/home/test", ["XDG_CONFIG_HOME"] = "relative" }, "Ubuntu", "/repo");
        Assert.Contains(files, f => f.Path == "/home/test/.config/opencode/opencode.json");
        Assert.Contains(files, f => f.Path == "/home/test/.codex/config.toml");
        Assert.Contains(files, f => f.Path == "/home/test/.claude/CLAUDE.md");
        Assert.Contains(files, f => f.Path == "/home/test/.claude.json");
    }
    [Fact]
    public void ProviderLoginCommandsAndWslTerminalArgumentsStaySeparate()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-manager-config", Guid.NewGuid().ToString("N")));
        var workspace = new Workspace("w", "Login", "/home/test/a b", "Debian");
        Assert.EndsWith(" login", AgentProviders.LoginCommand(store, workspace, AgentProvider.Codex));
        Assert.Equal("npx -y @anthropic-ai/claude-code@2.1.268 auth login", AgentProviders.LoginCommand(store, workspace, AgentProvider.Claude));
        Assert.Equal("npx -y opencode-ai@1.18.30 auth login", AgentProviders.LoginCommand(store, workspace, AgentProvider.OpenCode));
        if (!OperatingSystem.IsWindows()) return;
        var options = TerminalSession.Options(workspace, "opencode auth login");
        Assert.Contains("--exec", options.CommandLine);
        Assert.Equal(Hosts.WindowsArgument("exec opencode auth login"), options.CommandLine.Last());
        Assert.Contains(Hosts.WindowsArgument(workspace.Path), options.CommandLine);
    }
}
