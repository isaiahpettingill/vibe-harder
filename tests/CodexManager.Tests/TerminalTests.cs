using System.Text;
using Porta.Pty;

namespace CodexManager.Tests;

public class TerminalTests
{
    [Fact]
    public async Task CustomWindowsShellRunsQuotedExecutableThroughCmd()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "codex-shell", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var installed = TerminalPreferences.WindowsShells();
        Assert.All(installed, shell => Assert.True(File.Exists(shell.Executable)));
        var powershell = installed.First(s => s.Id is "pwsh" or "powershell");
        store.Setting("terminalShell:windows", "custom");
        store.Setting("terminalCommand:windows", $"\"{powershell.Executable}\" -NoLogo -NoProfile");
        await RoundTrip(new Workspace("w", "Custom", directory), store);
    }
    [Fact]
    public async Task ConfiguredWslShellRunsInItsWorkspace()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_WSL") != "1") return;
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-shell", Guid.NewGuid().ToString("N")));
        store.Setting("terminalCommand:wsl:Debian", "exec sh -i");
        await RoundTrip(new Workspace("w", "WSL", "/tmp", "Debian"), store);
    }
    [Fact]
    public void LoginUrlSurvivesSplitReadsAndAnsiWithoutLosingQueryParameters()
    {
        var output = new TerminalOutput();
        const string link = "https://auth.example.com/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A1455&state=abc%2Bdef#finish";
        output.Append(Encoding.UTF8.GetBytes("\u001b[32mSign in:\r\n" + link[..45]));
        Assert.Null(output.Link);
        output.Append(Encoding.UTF8.GetBytes(link[45..] + "\u001b[0m\r\n"));
        Assert.Equal(link, output.Link);
        Assert.DoesNotContain("\u001b", output.Text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task NativePtyAcceptsInputAndResizes()
    {
        var workspace = new Workspace("w", "Native", Path.GetTempPath());
        await RoundTrip(workspace);
    }
    [Fact]
    public async Task WslPtyStartsInLinuxWorkspace()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_WSL") != "1") return;
        await RoundTrip(new("w", "WSL", "/tmp", "Debian"));
    }
    private static async Task RoundTrip(Workspace workspace, Store? settings = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var options = TerminalSession.Options(workspace);
        if (settings is not null) TerminalPreferences.Apply(options, workspace, settings);
        using var terminal = await PtyProvider.SpawnAsync(options, timeout.Token);
        var output = new StringBuilder();
        try
        {
            terminal.Resize(110, 28);
            var read = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await terminal.ReaderStream.ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    var chunk = Encoding.UTF8.GetString(buffer, 0, count); output.Append(chunk);
                    // ConPTY asks its client for the cursor position before starting the shell.
                    if (chunk.Contains("\u001b[6n")) { await terminal.WriterStream.WriteAsync(Encoding.UTF8.GetBytes("\u001b[1;1R"), timeout.Token); await terminal.WriterStream.FlushAsync(timeout.Token); }
                    if (output.ToString().Contains("PTY_RESULT_42")) return output.ToString();
                }
                return output.ToString();
            }, timeout.Token);
            await Task.Delay(2500, timeout.Token);
            var command = workspace.IsWsl || !OperatingSystem.IsWindows() ? "printf 'PTY_RESULT_%s\\n' 42; pwd\r" : "Write-Output ('PTY_RESULT_' + 42); (Get-Location).Path\r";
            await terminal.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token); await terminal.WriterStream.FlushAsync(timeout.Token);
            Assert.Contains("PTY_RESULT_42", await read.WaitAsync(timeout.Token));
        }
        catch (OperationCanceledException) { Assert.Fail("PTY timed out. Output: " + output.ToString()); }
        finally { terminal.Kill(); }
    }
}
