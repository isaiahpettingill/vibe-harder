using System.Diagnostics;

namespace CodexManager.Tests;

public class WslShellTests
{
    [Fact]
    public async Task InteractivePathDoesNotLeakBannersOrConsumeAgentInput()
    {
        if (OperatingSystem.IsMacOS()) Assert.Skip("This launcher targets WSL/Linux.");
        if (OperatingSystem.IsWindows() && !(await Hosts.Distros()).Contains("Debian")) Assert.Skip("Debian WSL is not installed.");
        var script = """
            shell_file=$(mktemp)
            trap 'rm -f "$shell_file"' EXIT
            printf '%s\n' '#!/bin/sh' 'echo startup-banner' 'echo startup-error >&2' 'read -r ignored || :' 'printf "%s\n" "/bin:/usr/bin:/test-pi-bin" >&3' > "$shell_file"
            chmod +x "$shell_file"
            export SHELL="$shell_file"
            bash -c
            """ + " " + Hosts.Quote(Hosts.WslShellCommand("sh -c 'printf \"%s\\n\" \"$PATH\"; cat'"));
        var info = OperatingSystem.IsWindows() ? Hosts.Info("wsl.exe", "-d", "Debian", "--exec", "bash", "-c", script) : Hosts.Info("bash", "-c", script);
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteLineAsync("agent-input"); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        var text = await output;
        Assert.Contains("/test-pi-bin", text); Assert.Contains("agent-input", text);
        Assert.DoesNotContain("startup", text); Assert.Equal("", await errors);
    }

    [Fact]
    public async Task InstalledPiCanStartThroughTheWslBridge()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_PI") != "1") Assert.Skip("Opt-in installed Pi bridge smoke test.");
        var workspace = new Workspace("pi-smoke", "Pi", "/tmp", "Debian");
        await using var client = new AcpClient(AgentProviders.Start(workspace, AgentProviders.Get(AgentProvider.Pi).DefaultCommand, AgentProvider.Pi));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await client.Initialize(timeout.Token);
        var session = await client.Request("session/new", new() { ["cwd"] = "/tmp", ["mcpServers"] = new System.Text.Json.Nodes.JsonArray() }, timeout.Token);
        Assert.False(string.IsNullOrEmpty(session.GetProperty("sessionId").GetString()));
    }
}
