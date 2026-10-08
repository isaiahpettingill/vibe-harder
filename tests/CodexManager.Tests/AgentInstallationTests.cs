using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AgentInstallationTests
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WindowsLaunchUsesNpmCmdShimInsteadOfPowerShellScript()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("agent-shim-").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "opencode.cmd"), "@echo CMD_SHIM_READY\r\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "opencode.ps1"), "throw 'Wrong entry point'", TestContext.Current.CancellationToken);
        var start = Hosts.Agent(new Workspace("w", "Test", directory), "opencode --version");
        start.Environment["PATH"] = directory + Path.PathSeparator + start.Environment["PATH"];
        Assert.Contains("CMD_SHIM_READY", await Hosts.Capture(start));
    }

    // The guidance links to whatever is actually missing: Node for the npx bridges, otherwise the agent itself.
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(AgentProvider.OpenCode, "bash: opencode: command not found", "opencode.ai")]
    [InlineData(AgentProvider.Codex, "spawn codex ENOENT", "developers.openai.com")]
    [InlineData(AgentProvider.Claude, "The term 'claude' is not recognized as the name of a cmdlet", "code.claude.com")]
    [InlineData(AgentProvider.Claude, "npx.cmd : The term 'npx.cmd' is not recognized as the name of a cmdlet", "nodejs.org")]
    [InlineData(AgentProvider.Codex, "env: node: No such file or directory", "nodejs.org")]
    public void MissingExecutablesLinkToWhatIsMissing(AgentProvider provider, string error, string host)
    {
        var url = new Uri(AgentInstallation.FromError(provider, new IOException(error))!.Url);
        Assert.Equal(Uri.UriSchemeHttps, url.Scheme); Assert.Equal(host, url.Host);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("opencode: authentication required")]
    [InlineData("opencode: config.json not found")]
    [InlineData("spawn opencode EACCES")]
    [InlineData("npx: npm registry timed out")]
    public void OtherFailuresAreNotMissingInstallations(string error) => Assert.Null(AgentInstallation.FromError(AgentProvider.OpenCode, new IOException(error)));

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task MissingAgentShowsLinkWithoutRecoveryLoop()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "missing-agent-" + Guid.NewGuid().ToString("N")));
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.OpenCode };
        store.Save(workspace); store.Save(chat);
        var command = OperatingSystem.IsWindows() ? "[Console]::Error.WriteLine('opencode: command not found'); exit 127" : "printf 'opencode: command not found\\n' >&2; exit 127";
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        await runtime.Send("hello", []);
        var guidance = AgentInstallation.FromError(chat.Provider, new IOException("opencode: command not found"))!;
        Assert.Contains(chat.Messages, m => m.Text.Contains(guidance.Url));
        Assert.False(runtime.IsRecovering); Assert.False(chat.Busy);
    }
}
