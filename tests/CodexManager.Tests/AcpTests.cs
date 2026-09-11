using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AcpTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    [Fact]
    public async Task CancellationInterruptsWritesToAnAgentThatNeverReads()
    {
        await using var client = new AcpClient(Hosts.Info("node", "-e", "setInterval(() => {}, 1000)"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Request("echo",
            RpcJson.Object(("text", new string('x', 4 * 1024 * 1024))), timeout.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task RpcCorrelatesConcurrentRequestsAndPropagatesExit()
    {
        await using var client = new AcpClient(Hosts.Info("node", Fixture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.Initialize(timeout.Token);
        var tasks = Enumerable.Range(0, 12).Select(i => client.Request("echo", RpcJson.Object(("number", i)), timeout.Token)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 12), results.Select(r => r.GetProperty("number").GetInt32()));
        await Assert.ThrowsAsync<IOException>(() => client.Request("crash", new JsonObject(), timeout.Token));
    }
    [Fact]
    public async Task PermissionRoundTripKeepsReaderAvailable()
    {
        await using var client = new AcpClient(Hosts.Info("node", Fixture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var asked = false;
        client.PermissionRequested = (_, _) => { asked = true; return Task.FromResult<JsonObject>(RpcJson.Permission("reject")); };
        await client.Initialize(timeout.Token);
        var response = await client.Request("session/prompt", RpcJson.Object(("sessionId", "fixture-session"), ("prompt", new JsonArray(RpcJson.Object(("type", "text"), ("text", "permission"))))), timeout.Token);
        Assert.True(asked); Assert.Equal("end_turn", response.GetProperty("stopReason").GetString());
    }
    [AvaloniaFact]
    public async Task RuntimeStreamsResumesWithoutReplayAndInterrupts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-manager-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Fixture", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var command = OperatingSystem.IsWindows() ? $"node '{Fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(Fixture)}";
        await using (var runtime = new ChatRuntime(chat, workspace, store, command))
        {
            await runtime.Send("hello", []).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("fixture-session", chat.SessionId);
            Assert.Equal("Hello **world**", chat.Messages.Last().Text);
            Assert.False(chat.Busy);
        }
        await using var resumed = new ChatRuntime(chat, workspace, store, command);
        await resumed.Send("again", []).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("REPLAY"));
        Assert.Equal(4, chat.Messages.Count);
        var pending = resumed.Send("hang", []);
        while (chat.Status != "Working…") await Task.Delay(10);
        var stop = resumed.Stop();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(chat.Busy); Assert.Equal("Interrupted", chat.Status);
        await stop;
    }
    [Fact]
    public void AttachmentEncodesImageAndEmbeddedTextAsAcpBlocks()
    {
        var image = JsonSerializer.SerializeToElement(new Attachment("image.png", "image/png", "AQID").ToContent());
        Assert.Equal("image", image.GetProperty("type").GetString()); Assert.Equal("AQID", image.GetProperty("data").GetString());
        var file = JsonSerializer.SerializeToElement(new Attachment("source.cs", "text/plain", "class A {}", Path.GetFullPath("source.cs")).ToContent());
        Assert.Equal("class A {}", file.GetProperty("resource").GetProperty("text").GetString());
    }
}
