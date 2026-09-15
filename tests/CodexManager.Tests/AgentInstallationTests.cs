using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AgentInstallationTests
{
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
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"), start.Environment["PATH"]);
    }

    [Theory]
    [InlineData(AgentProvider.OpenCode, "bash: opencode: command not found", "Install opencode to use opencode ACP")]
    [InlineData(AgentProvider.Codex, "spawn codex ENOENT", "Install Codex to use Codex ACP")]
    [InlineData(AgentProvider.Claude, "The term 'claude' is not recognized as the name of a cmdlet", "Install Claude Code to use Claude Code ACP")]
    [InlineData(AgentProvider.Claude, "npx.cmd : The term 'npx.cmd' is not recognized as the name of a cmdlet", "Node is required to run Codex/Claude ACP bridges")]
    [InlineData(AgentProvider.Codex, "env: node: No such file or directory", "Node is required to run Codex/Claude ACP bridges")]
    public void MissingExecutablesGiveSpecificInstallGuidance(AgentProvider provider, string error, string expected)
    { var result = AgentInstallation.FromError(provider, new IOException(error)); Assert.Equal(expected, result?.Message); Assert.StartsWith("https://", result!.Url); }

    [Theory]
    [InlineData("opencode: authentication required")]
    [InlineData("opencode: config.json not found")]
    [InlineData("spawn opencode EACCES")]
    [InlineData("npx: npm registry timed out")]
    public void OtherFailuresAreNotMissingInstallations(string error) => Assert.Null(AgentInstallation.FromError(AgentProvider.OpenCode, new IOException(error)));

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
        Assert.Equal("Install opencode to use opencode ACP", chat.Status);
        Assert.Contains(chat.Messages, m => m.Text.Contains("https://opencode.ai/download"));
        Assert.False(runtime.IsRecovering); Assert.False(chat.Busy);
    }
}
