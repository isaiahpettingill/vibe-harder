using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class PiFailureTests
{
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
