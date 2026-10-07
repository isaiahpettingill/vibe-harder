using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class UnifiedPushHostTests
{
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    [Trait("Category", "Slow")]
    [AvaloniaFact]
    public async Task PairedPhoneRegistersOverItsConnectionAndGetsEncryptedPushes()
    {
        var directory = Directory.CreateTempSubdirectory("push-host-").FullName; var port = Port();
        await using var server = new RemoteServer(directory, "127.0.0.1", port, _ => Task.FromResult<JsonNode?>(JsonValue.Create(true)));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (server.Fingerprint is null && DateTime.UtcNow < until) await Task.Delay(25);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "phone.key"), "Phone", TestContext.Current.CancellationToken);
        using var connection = new RemoteConnection(host);
        await connection.Connect(TestContext.Current.CancellationToken);
        var device = Assert.Single(RemoteTrust.Devices(directory)).Key;

        // The phone learns the computer's VAPID key, registers with its distributor, and sends the endpoint back.
        var vapid = (await connection.Request(new() { ["method"] = "push/vapid" }, TestContext.Current.CancellationToken))!["publicKey"]!.GetValue<string>();
        Assert.Equal(87, vapid.Length); Assert.Equal(vapid, WebPush.Base64Url(RemoteTrust.VapidKey(directory).PublicKey));
        var phone = WebPush.Generate(); var auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        JsonObject Register(string endpoint) => new() { ["method"] = "push/register", ["endpoint"] = endpoint, ["p256dh"] = WebPush.Base64Url(phone.PublicKey), ["auth"] = WebPush.Base64Url(auth), ["distributor"] = "org.unifiedpush.distributor.sunup" };
        await connection.Request(Register("https://push.example.net/wpush/v2/first"), TestContext.Current.CancellationToken);
        // A changed endpoint replaces the old one.
        await connection.Request(Register("https://push.example.net/wpush/v2/second"), TestContext.Current.CancellationToken);
        var target = Assert.Single(RemoteTrust.PushTargets(directory));
        Assert.Equal((device, "https://push.example.net/wpush/v2/second"), (target.Device, target.Push["endpoint"]!.GetValue<string>()));
        await Assert.ThrowsAsync<RemoteOperationException>(() => connection.Request(new() { ["method"] = "push/register", ["endpoint"] = "ftp://x/y", ["p256dh"] = "AA", ["auth"] = "AA" }, TestContext.Current.CancellationToken));

        var sent = new List<(Uri Endpoint, Dictionary<string, string> Headers, byte[] Body)>();
        var status = HttpStatusCode.Created;
        var original = UnifiedPushHost.Send;
        UnifiedPushHost.Send = async request =>
        {
            var headers = request.Headers.Concat(request.Content!.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            sent.Add((request.RequestUri!, headers, await request.Content.ReadAsByteArrayAsync()));
            return new HttpResponseMessage(status);
        };
        try
        {
            await UnifiedPushHost.Notify(directory, "Reply ready", "Fix login", "chat-1");
            var push = Assert.Single(sent);
            Assert.Equal("https://push.example.net/wpush/v2/second", push.Endpoint.ToString());
            Assert.Equal("aes128gcm", push.Headers["Content-Encoding"]); Assert.Equal("86400", push.Headers["TTL"]);
            Assert.StartsWith("vapid t=", push.Headers["Authorization"]); Assert.EndsWith("k=" + vapid, push.Headers["Authorization"]);
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(WebPush.Decrypt(push.Body, phone, auth)))!;
            Assert.Equal(("Reply ready", "Fix login", "chat-1"), (payload["title"]!.GetValue<string>(), payload["body"]!.GetValue<string>(), payload["chat"]!.GetValue<string>()));

            // A push server that forgot the subscription removes it.
            status = HttpStatusCode.Gone;
            await UnifiedPushHost.Notify(directory, "Reply ready", "Again", null);
            Assert.Empty(RemoteTrust.PushTargets(directory));
            Assert.True(RemoteTrust.Authorized(directory, device, JsonNode.Parse(File.ReadAllText(host.KeyPath))!["token"]!.GetValue<string>()));

            // Unregistering and revoking both remove it; pairing itself is unaffected by push.
            await connection.Request(Register("https://push.example.net/wpush/v2/third"), TestContext.Current.CancellationToken);
            await connection.Request(new() { ["method"] = "push/unregister" }, TestContext.Current.CancellationToken);
            Assert.Empty(RemoteTrust.PushTargets(directory));
            await connection.Request(Register("https://push.example.net/wpush/v2/fourth"), TestContext.Current.CancellationToken);
            RemoteTrust.Revoke(directory, device);
            Assert.Empty(RemoteTrust.PushTargets(directory));
        }
        finally { UnifiedPushHost.Send = original; }
    }
}
