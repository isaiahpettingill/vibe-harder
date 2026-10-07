namespace CodexManager.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Debian")]
    public void CodexBridgeUpgradesSavedShippedDefaultButPreservesCustomCommands(string? distro)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-command-").FullName);
        var workspace = new Workspace("w", "Test", "/tmp", distro);
        var key = AgentProviders.CommandKey(AgentProvider.Codex, workspace.IsWsl);
        store.Setting(key, "npx -y @agentclientprotocol/codex-acp@1.11.0");
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        Assert.Equal(Hosts.DefaultAdapter, store.Setting(key));
        const string custom = "my-wrapper npx -y @agentclientprotocol/codex-acp@1.11.0 --custom";
        store.Setting(key, custom);
        Assert.Equal(custom, AgentProviders.Command(store, workspace, AgentProvider.Codex));
    }

    [Fact]
    public void CodexAdapterRunsTheLatestCodexInsteadOfItsPinnedCopy()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-latest-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var key = AgentProviders.CommandKey(AgentProvider.Codex, false);
        store.Setting(key, "npx -y @agentclientprotocol/codex-acp@latest");
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        Assert.Contains("--package=@openai/codex@latest", Hosts.DefaultAdapter);
        Assert.Equal("codex", AgentProviders.Start(workspace, Hosts.DefaultAdapter, AgentProvider.Codex).Environment["CODEX_PATH"]);
        // A custom launcher without its own Codex package must keep the adapter's bundled binary.
        Assert.False(AgentProviders.Start(workspace, "my-wrapper codex-acp", AgentProvider.Codex).Environment.ContainsKey("CODEX_PATH"));
        // Launches run the pinned set once the updater has installed it; the settings field does not.
        BundledPackages.Pin(store, workspace, new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.0.0", ["@openai/codex"] = "0.159.1" });
        var pinned = AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex);
        Assert.Equal("npx -y --prefer-offline --package=@agentclientprotocol/codex-acp@2.0.0 --package=@openai/codex@0.159.1 -- codex-acp", pinned);
        Assert.Equal("codex", AgentProviders.Start(workspace, pinned, AgentProvider.Codex).Environment["CODEX_PATH"]);
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        Assert.Equal("npx -y --package=@agentclientprotocol/codex-acp@2.0.0 --package=@openai/codex@0.159.1 -- codex --version",
            BundledPackages.Install(AgentProvider.Codex, new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.0.0", ["@openai/codex"] = "0.159.1" }));
        // Pins apply per environment and never to custom commands.
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.LaunchCommand(store, new Workspace("d", "WSL", "/tmp", "Debian"), AgentProvider.Codex));
        store.Setting(key, "my-wrapper codex-acp");
        Assert.Equal("my-wrapper codex-acp", AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex));
    }

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
        Assert.EndsWith("exec " + command, Hosts.Agent(wsl, command).ArgumentList.Last());
        Assert.Contains("$HOME/.opencode/bin", Hosts.Agent(wsl, command).ArgumentList.Last());
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
        var workspace = new Workspace("w", "Login", "/home/test/a b", "Debian");
        if (!OperatingSystem.IsWindows()) return;
        var options = TerminalSession.Options(workspace, "opencode auth login");
        Assert.Contains("--exec", options.CommandLine);
        Assert.Equal(Hosts.WindowsArgument("exec opencode auth login"), options.CommandLine.Last());
        Assert.Contains(Hosts.WindowsArgument(workspace.Path), options.CommandLine);
    }
}
