using System.Diagnostics;

namespace CodexManager.Tests;

public class WslShellTests
{
    [Fact]
    public async Task LaunchNeverRunsTheInteractiveShellAndFindsNvmDefaultNode()
    {
        if (OperatingSystem.IsMacOS()) Assert.Skip("This launcher targets WSL/Linux.");
        var distro = OperatingSystem.IsWindows() ? (await Hosts.Distros()).FirstOrDefault() : null;
        if (OperatingSystem.IsWindows() && distro is null) Assert.Skip("No WSL distro is installed.");
        // An interactive shell that prints a banner and then hangs: a launch must neither wait on it nor show its output.
        var script = """
            home=$(mktemp -d)
            trap 'rm -rf "$home"' EXIT
            mkdir -p "$home/.nvm/versions/node/v20.1.0/bin" "$home/.nvm/versions/node/v22.3.0/bin" "$home/.nvm/alias"
            echo 20 > "$home/.nvm/alias/default"
            printf '%s\n' '#!/bin/sh' 'echo startup-banner' 'sleep 600' > "$home/shell"
            chmod +x "$home/shell"
            export SHELL="$home/shell" HOME="$home"
            unset NVM_DIR PNPM_HOME FNM_DIR
            bash -c
            """ + " " + Hosts.Quote(Hosts.WslShellCommand("sh -c 'printf \"%s\\n\" \"$PATH\"; cat'"));
        var info = OperatingSystem.IsWindows() ? Hosts.Info("wsl.exe", "-d", distro!, "--exec", "bash", "-c", script) : Hosts.Info("bash", "-c", script);
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteLineAsync("agent-input"); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        var text = await output;
        Assert.Contains("/.nvm/versions/node/v20.1.0/bin", text); Assert.DoesNotContain("v22.3.0", text);
        Assert.Contains("agent-input", text); Assert.DoesNotContain("startup", text); Assert.Equal("", await errors);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), timer.Elapsed.ToString());
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
