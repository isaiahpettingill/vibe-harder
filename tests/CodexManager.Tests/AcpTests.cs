using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AcpTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    [Trait("Category", "CI")]
    [Fact]
    public async Task CancellationInterruptsWritesToAnAgentThatNeverReads()
    {
        await using var client = new AcpClient(Hosts.Info("node", "-e", "setInterval(() => {}, 1000)"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Request("echo",
            RpcJson.Object(("text", new string('x', 4 * 1024 * 1024))), timeout.Token).WaitAsync(TimeSpan.FromSeconds(20)));
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task UncancellableWritesToAStuckAgentFailInsteadOfHanging()
    {
        // Notify (used by Stop) has no token; a stuck agent must not hang it or the caller.
        var client = new AcpClient(Hosts.Info("node", "-e", "setInterval(() => {}, 1000)")) { WriteTimeout = TimeSpan.FromMilliseconds(500) };
        await Assert.ThrowsAsync<IOException>(() => client.Notify("session/cancel", RpcJson.Object(("text", new string('x', 4 * 1024 * 1024)))).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(client.Alive);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Request("echo", new JsonObject()).WaitAsync(TimeSpan.FromSeconds(5)));
        var id = ((System.Diagnostics.Process)typeof(AcpClient).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!).Id;
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        // The stuck agent must actually be killed, not left running with its pipe held open.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool Running() { try { using var agent = System.Diagnostics.Process.GetProcessById(id); return !agent.HasExited; } catch (ArgumentException) { return false; } }
        while (Running() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(Running());
    }
    // An agent that writes `banner` to stdout in `encoding`, then either exits with 255 or echoes requests.
    private static AcpClient BannerAgent(string banner, string encoding, bool exit) => new(Hosts.Info("node", "-e",
        $"process.stdout.write(Buffer.from({JsonSerializer.Serialize(banner)}, '{encoding}'));" +
        (exit ? "setTimeout(() => process.exit(255), 100);" :
            "require('readline').createInterface({ input: process.stdin }).on('line', l => { const m = JSON.parse(l); " +
            "process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: m.id, result: m.params }) + '\\n'); });")));
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("Welcome to Ubuntu\n", "utf8")]
    [InlineData("conda: environment activated\r\n\n  \n", "utf8")]
    [InlineData("Catastrophic failure\r\n", "utf16le")]
    public async Task PlainTextOnStdoutDoesNotBreakTheConnection(string banner, string encoding)
    {
        await using var client = BannerAgent(banner, encoding, exit: false);
        var result = await client.Request("echo", RpcJson.Object(("number", 7))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, result.GetProperty("number").GetInt32());
        Assert.True(client.Alive);
    }
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("Catastrophic failure\r\n\r\nError code: Wsl/Service/E_UNEXPECTED\r\n", "utf16le", "Wsl/Service/E_UNEXPECTED")]
    [InlineData("bash: codex-acp: command not found\n", "utf8", "command not found")]
    public async Task LauncherFailuresOnStdoutAreReportedAsTheDisconnectReason(string banner, string encoding, string reported)
    {
        // wsl.exe writes its own failures to stdout in UTF-16; they must reach the user, not a JSON parse error.
        await using var client = BannerAgent(banner, encoding, exit: true);
        var error = await Assert.ThrowsAsync<IOException>(() => client.Request("echo", new JsonObject()).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(reported, error.Message);
        Assert.Contains(reported, client.DisconnectReason);
    }
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
    [Fact]
    public async Task FormElicitationAdvertisesCapabilityAndReturnsStructuredAnswer()
    {
        await using var client = new AcpClient(Hosts.Info("node", Fixture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        client.ElicitationRequested = (request, _) =>
        {
            Assert.Equal("form", request.GetProperty("mode").GetString());
            return Task.FromResult(ElicitationForm.Accept(new JsonObject { ["approach"] = "broad", ["note"] = "Keep tests" }));
        };
        await client.Initialize(timeout.Token);
        var capabilities = await client.Request("fixture/capabilities", new JsonObject(), timeout.Token);
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("elicitation").GetProperty("form").ValueKind);
        Assert.False(capabilities.GetProperty("elicitation").TryGetProperty("url", out _));
        var chunks = new List<string>(); client.Update += update => { if (update.TryGetProperty("content", out var content)) chunks.Add(content.GetProperty("text").GetString()!); };
        var result = await client.Request("session/prompt", RpcJson.Object(("sessionId", "fixture-session"), ("prompt", new JsonArray(RpcJson.Object(("type", "text"), ("text", "question"))))), timeout.Token);
        Assert.Equal("end_turn", result.GetProperty("stopReason").GetString());
        Assert.Contains(chunks, text => text.Contains("\"action\":\"accept\"") && text.Contains("\"approach\":\"broad\""));
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task FormElicitationIsNotAdvertisedWithoutAHandler()
    {
        await using var client = new AcpClient(Hosts.Info("node", Fixture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.Initialize(timeout.Token);
        var capabilities = await client.Request("fixture/capabilities", new JsonObject(), timeout.Token);
        Assert.False(capabilities.TryGetProperty("elicitation", out _));
    }
    [Trait("Category", "Integration")]
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
        Assert.False(chat.Busy);
        await stop;
    }
}
