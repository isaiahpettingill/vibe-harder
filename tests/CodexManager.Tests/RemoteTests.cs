using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class RemoteTests
{
    [Fact]
    public async Task RemoteQuestionAppearsAboveComposerAndAnswerIsValidated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-question-test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory);
        var chat = new Chat { WorkspaceId = "w" };
        await using var runtime = new ChatRuntime(chat, workspace, store, "node");
        using var service = new SessionService(store, new List<Workspace> { workspace }, new List<Chat> { chat }, (_, _) => runtime);
        using var document = JsonDocument.Parse("""
            {"mode":"form","message":"Choose","requestedSchema":{"type":"object","properties":{"option":{"type":"string","enum":["one","two"]}},"required":["option"]}}
            """);
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = service.RegisterElicitation(chat, document.RootElement, pending);
        var snapshot = await service.Handle(new JsonObject { ["method"] = "chat", ["chatId"] = chat.Id, ["activate"] = false });
        Assert.Equal(id, snapshot!["elicitations"]!.AsArray().Single()!["id"]!.GetValue<string>());
        await Assert.ThrowsAsync<IOException>(() => service.Handle(new JsonObject
        {
            ["method"] = "elicitation/respond",
            ["elicitationId"] = id,
            ["response"] = ElicitationForm.Accept(new JsonObject { ["option"] = "bad" })
        }));
        Assert.False(pending.Task.IsCompleted);
        await service.Handle(new JsonObject
        {
            ["method"] = "elicitation/respond",
            ["elicitationId"] = id,
            ["response"] = ElicitationForm.Accept(new JsonObject { ["option"] = "two" })
        });
        Assert.Equal("two", (await pending.Task)["content"]!["option"]!.GetValue<string>());
        service.ForgetElicitation(id);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedClientPersistsAcrossRestartsAndRejectsWrongIdentity(bool restartHost)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-remote-test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var store = new Store(directory, backgroundWrites: true); var workspace = new Workspace("w", "Remote test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var service = new SessionService(store, new List<Workspace> { workspace }, new List<Chat> { chat }, (_, _) => runtime);
        runtime.Permission = (request, token) => service.Permission(chat, request, token);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var initialServer = new RemoteServer(directory, "127.0.0.1", port, service.Handle);
        RemoteServer server = initialServer;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (server.Fingerprint is null && server.Error is null) await Task.Delay(25, timeout.Token);
        Assert.Null(server.Error);
        var code = RemoteTrust.Invite(directory, "127.0.0.1", port, "Test");
        var host = await RemoteConnection.Pair(code, Path.Combine(directory, "client.json"), "Test device", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => RemoteConnection.Pair(code, Path.Combine(directory, "duplicate.json"), "Duplicate", timeout.Token));
        if (restartHost)
        {
            await server.DisposeAsync();
            server = new RemoteServer(directory, "127.0.0.1", port, service.Handle);
            while (server.Fingerprint is null && server.Error is null) await Task.Delay(25, timeout.Token);
            Assert.Equal(host.Fingerprint, server.Fingerprint);
        }
        await using var restartedServer = restartHost ? server : null;
        using (var wrong = new RemoteConnection(host with { Fingerprint = "SHA256:wrong" }))
            await Assert.ThrowsAnyAsync<Exception>(() => wrong.Connect(timeout.Token));
        using (var first = new RemoteConnection(host))
        {
            await first.Connect(timeout.Token);
            var list = await first.Request(new() { ["method"] = "list" }, timeout.Token);
            Assert.Single(list!["workspaces"]!.AsArray());
            await first.Request(new() { ["method"] = "send", ["chatId"] = chat.Id, ["text"] = "hang" }, timeout.Token);
            while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20, timeout.Token);
        }
        Assert.True(chat.Busy);
        using (var second = new RemoteConnection(host))
        {
            await second.Connect(timeout.Token);
            var snapshot = await second.Request(new() { ["method"] = "chat", ["chatId"] = chat.Id }, timeout.Token);
            Assert.True(snapshot!["busy"]!.GetValue<bool>());
            await second.Request(new() { ["method"] = "stop", ["chatId"] = chat.Id }, timeout.Token);
            Assert.False(chat.Busy);
        }
        var devices = RemoteTrust.Devices(directory);
        using var connected = new RemoteConnection(host);
        await connected.Connect(timeout.Token);
        RemoteTrust.Revoke(directory, devices.Single().Key);
        await Assert.ThrowsAnyAsync<Exception>(() => connected.Request(new() { ["method"] = "list" }, timeout.Token));
        using var revoked = new RemoteConnection(host);
        await Assert.ThrowsAnyAsync<Exception>(() => revoked.Connect(timeout.Token));
    }
    [Fact]
    public async Task RejectsOversizedFrameBeforeReadingPayload()
    {
        using var stream = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff });
        await Assert.ThrowsAsync<IOException>(() => RemoteWire.Read(stream, CancellationToken.None));
    }
    [Fact]
    public void ExpiredInviteCannotAuthorizeADevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-expired", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var secret = RemoteKey.NewSecret();
        File.WriteAllText(Path.Combine(directory, "pairing.json"), new JsonObject { ["hash"] = RemoteKey.Hash(secret), ["expires"] = 0 }.ToJsonString());
        Assert.Throws<IOException>(() => RemoteTrust.Pair(directory, secret, "Device"));
        Assert.Empty(RemoteTrust.Devices(directory));
    }
}
