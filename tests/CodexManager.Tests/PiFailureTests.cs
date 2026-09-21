using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class PiFailureTests
{
    [Fact]
    public async Task PiLogProbeFindsExpiredLoginButIgnoresPreviousTurns()
    {
        var directory = Directory.CreateTempSubdirectory("pi-log-").FullName;
        var logs = Path.Combine(directory, ".pi", "pi-acp"); Directory.CreateDirectory(logs);
        var file = Path.Combine(directory, "session.jsonl");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var error = "OAuth refresh failed: refresh_token_expired. Please log in again.";
        await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(new { message = new { role = "assistant", timestamp = now, stopReason = "error", errorMessage = error } }), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "session-map.json"), System.Text.Json.JsonSerializer.Serialize(new { sessions = new { test = new { sessionFile = file } } }), TestContext.Current.CancellationToken);
        var probe = (string)typeof(PiDiagnostics).GetField("Probe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue()!;
        async Task<string> Read(long since) => await Hosts.Capture(Hosts.Info("node", "-e", "require('node:os').homedir=()=>" + System.Text.Json.JsonSerializer.Serialize(directory) + ";const sessionId='test',since=" + since + ";" + probe));
        Assert.Equal(error, await Read(now - 1));
        Assert.Equal("", await Read(now + 1));
        Assert.True(AgentProviders.IsAuthenticationError(new IOException(error)));
    }

    [AvaloniaFact]
    public async Task SilentPiTurnShowsFailureAndKeepsInputAndQueue()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("pi-failure-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Pi }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --silent-turn");
        runtime.Queue(new("Keep queued", []));
        await runtime.Send("Do the work", []);
        Assert.Contains(chat.Messages, m => m.Role == "system" && m.Text.Contains("Pi ended the turn without a response"));
        Assert.Equal("Do the work", chat.Draft);
        Assert.NotNull(chat.InterruptedInput);
        Assert.Single(chat.QueuedInputs);
        Assert.False(chat.HasUnreadCompletion);
    }

    [Fact]
    public async Task ProcessExitIncludesStderrAndExitCode()
    {
        await using var client = new AcpClient(Hosts.Info("node", "-e", "process.stdin.once('data',()=>{process.stderr.write('Pi startup failed',()=>process.exit(7));})"));
        var error = await Assert.ThrowsAsync<IOException>(() => client.Initialize(TestContext.Current.CancellationToken));
        Assert.Contains("Pi startup failed", error.Message);
        Assert.Contains("7", error.Message);
    }
}
