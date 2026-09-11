using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class RemoteTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedClientReconnectsToHostOwnedTurnAndRejectsWrongIdentity(bool rsa)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-remote-test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var generate = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
        generate.ArgumentList.Add("-e"); generate.ArgumentList.Add("const {utils}=require('ssh2'),fs=require('fs'),p=process.argv[1],k=utils.generateKeyPairSync('ed25519');fs.writeFileSync(p+'/client',k.private);fs.writeFileSync(p+'/authorized_keys',k.public);"); generate.ArgumentList.Add(directory);
        using (var process = Process.Start(generate)!) { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
        if (rsa) { var publicKey = RemoteKey.Generate(Path.Combine(directory, "client-rsa")); File.Copy(Path.Combine(directory, "client-rsa"), Path.Combine(directory, "client"), true); File.WriteAllText(Path.Combine(directory, "authorized_keys"), publicKey); }
        using var store = new Store(directory); var workspace = new Workspace("w", "Remote test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var service = new SessionService(store, new List<Workspace> { workspace }, new List<Chat> { chat }, (_, _) => runtime);
        runtime.Permission = (request, token) => service.Permission(chat, request, token);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var server = new RemoteServer(directory, "127.0.0.1", port, service.Handle);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (server.Fingerprint is null && server.Error is null) await Task.Delay(25, timeout.Token);
        Assert.Null(server.Error);
        var host = new RemoteHost("Test", "127.0.0.1", port, Path.Combine(directory, "client"), server.Fingerprint!);
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
        File.WriteAllText(Path.Combine(directory, "authorized_keys"), "");
        using var revoked = new RemoteConnection(host);
        await Assert.ThrowsAnyAsync<Exception>(() => revoked.Connect(timeout.Token));
    }
}
