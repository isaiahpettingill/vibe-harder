using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class DiracExtensionTests
{
    private static string Command => "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --dirac";
    [AvaloniaFact]
    public async Task AdvertisedWhisperSteersWithoutEndingTheActiveTurn()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("dirac-steer-").FullName);
        var workspace = new Workspace("w", "Fixture", store.DirectoryPath);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Dirac }; store.Save(workspace); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Command);
        var turn = runtime.Send("hang", []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20, timeout.Token);
        Assert.True(runtime.SupportsSteering);
        Assert.True(await runtime.Steer(new PendingInput("Use the other approach", [])).WaitAsync(timeout.Token));
        Assert.True(runtime.IsPrompting); Assert.False(turn.IsCompleted);
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text == "Use the other approach");
        await runtime.Stop(); await turn;
    }
    [AvaloniaFact]
    public async Task CheckpointRestoreValidatesIdAndReloadsHistory()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("dirac-checkpoint-").FullName);
        var workspace = new Workspace("w", "Fixture", store.DirectoryPath);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Dirac }; store.Save(workspace); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, Command);
        await runtime.Send("hello", []);
        Assert.True(runtime.SupportsCheckpoints);
        await Assert.ThrowsAsync<IOException>(() => runtime.RestoreCheckpoint("invalid"));
        Assert.Contains(chat.Messages, m => m.Text.Contains("Hello"));
        runtime.Queue(new PendingInput("later", []));
        await runtime.RestoreCheckpoint("checkpoint-1");
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("Hello"));
        Assert.Contains(chat.Messages, m => m.Text == "REPLAY SHOULD NOT DUPLICATE");
        Assert.Empty(chat.QueuedInputs); Assert.False(chat.Busy); Assert.False(runtime.IsChangingHistory);
        Assert.Equal("0", store.Setting("historyIncomplete:" + chat.Id));
        Assert.Equal("0", store.Setting("busy:" + chat.Id));
    }
}
