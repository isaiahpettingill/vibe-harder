using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace CodexManager;

public sealed class RemoteServer : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
    private TcpListener? listener;
    private readonly Task loop;
    private readonly SemaphoreSlim pairingGate = new(1, 1);
    private readonly Queue<DateTimeOffset> pairingRequests = new();
    public string? Fingerprint { get; private set; }
    public string? Error { get; private set; }
    public static string DirectoryPath => Path.Combine(Store.DataDirectory, "remote");
    public RemoteServer(string directory, string address, int port, Func<JsonObject, Task<JsonNode?>> handle, Func<string, string, CancellationToken, Action, Task>? showPairing = null)
    {
        loop = Task.Run(async () =>
        {
            try
            {
                using var certificate = RemoteTrust.Certificate(directory);
                // Listening interfaces and client-facing DNS names are separate settings.
                var bindAddress = IPAddress.TryParse(address, out var ip) ? ip : IPAddress.Any;
                if ((bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.IPv6Any)) && Socket.OSSupportsIPv6) bindAddress = IPAddress.IPv6Any;
                listener = new TcpListener(bindAddress, port);
                if (bindAddress.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
                listener.Start();
                Fingerprint = RemoteTrust.Fingerprint(certificate);
                await File.WriteAllTextAsync(Path.Combine(directory, "host_fingerprint"), Fingerprint + "\n", lifetime.Token);
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                        if (clients.Count >= 32) { client.Dispose(); continue; }
                        var task = Serve(client, certificate); clients[client] = task;
                        _ = task.ContinueWith(_ => { clients.TryRemove(client, out var ignored); client.Dispose(); }, TaskScheduler.Default);
                    }
                }
                finally { listener.Stop(); foreach (var client in clients.Keys) client.Dispose(); await Task.WhenAll(clients.Values); }
            }
            catch (Exception error) { if (!lifetime.IsCancellationRequested) Error = error.Message; }
        });
        async Task Serve(TcpClient client, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
        {
            using var stream = new SslStream(client.GetStream(), false);
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, handshake.Token);
                var login = await RemoteWire.Read(stream, handshake.Token, 16 * 1024);
                if (login["method"]?.GetValue<string>() == "pairing/start")
                {
                    var response = new JsonObject { ["id"] = login["id"]?.DeepClone() };
                    if (showPairing is null || !await pairingGate.WaitAsync(0, lifetime.Token))
                    {
                        response["error"] = showPairing is null ? "Pairing is unavailable on this host." : "Another device is pairing. Try again shortly.";
                        await RemoteWire.Write(stream, response, handshake.Token); return;
                    }
                    using var pairing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    pairing.CancelAfter(TimeSpan.FromMinutes(2));
                    try
                    {
                        while (pairingRequests.TryPeek(out var time) && time < DateTimeOffset.UtcNow.AddMinutes(-1)) pairingRequests.Dequeue();
                        if (pairingRequests.Count >= 5) throw new IOException("Too many pairing requests. Try again in a minute.");
                        pairingRequests.Enqueue(DateTimeOffset.UtcNow);
                        var challenge = new RemotePairingChallenge(RemoteTrust.Fingerprint(certificate));
                        var name = login["name"]?.GetValue<string>() ?? "Device";
                        name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray());
                        await showPairing(name, challenge.Code, pairing.Token, pairing.Cancel);
                        response["result"] = challenge.Offer();
                        await RemoteWire.Write(stream, response, pairing.Token);
                        var proof = await RemoteWire.Read(stream, pairing.Token, 16 * 1024);
                        response = new JsonObject { ["id"] = proof["id"]?.DeepClone() };
                        if (proof["method"]?.GetValue<string>() != "pairing/finish") throw new IOException("Invalid pairing request.");
                        var evidence = challenge.Verify(proof);
                        pairing.Token.ThrowIfCancellationRequested();
                        response["result"] = new JsonObject { ["proof"] = evidence, ["credential"] = RemoteTrust.AddDevice(directory, name) };
                        await RemoteWire.Write(stream, response, pairing.Token);
                    }
                    catch (Exception error) when (!pairing.IsCancellationRequested)
                    {
                        response.Remove("result"); response["error"] = error.Message;
                        await RemoteWire.Write(stream, response, pairing.Token);
                    }
                    finally { pairing.Cancel(); pairingGate.Release(); }
                    return;
                }
                var reply = new JsonObject { ["id"] = login["id"]?.DeepClone() };
                string device; string secret;
                try
                {
                    if (login["method"]?.GetValue<string>() == "pair")
                    {
                        var credential = RemoteTrust.Pair(directory, login["code"]!.GetValue<string>(), login["name"]?.GetValue<string>() ?? "Device");
                        device = credential["device"]!.GetValue<string>(); secret = credential["token"]!.GetValue<string>(); reply["result"] = credential;
                    }
                    else
                    {
                        device = login["device"]?.GetValue<string>() ?? ""; secret = login["token"]?.GetValue<string>() ?? "";
                        if (login["method"]?.GetValue<string>() != "auth" || !RemoteTrust.Authorized(directory, device, secret)) throw new IOException("Device is not authorized. Pair with this host again.");
                        reply["result"] = true;
                    }
                }
                catch (Exception error) { reply["error"] = error.Message; await RemoteWire.Write(stream, reply, handshake.Token); return; }
                await RemoteWire.Write(stream, reply, handshake.Token);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var revocation = Task.Run(async () =>
                {
                    try { while (!session.IsCancellationRequested) { await Task.Delay(5000, session.Token); if (!RemoteTrust.Authorized(directory, device, secret)) { session.Cancel(); client.Dispose(); } } }
                    catch (OperationCanceledException) { }
                });
                try
                {
                    while (!session.IsCancellationRequested)
                    {
                        var request = await RemoteWire.Read(stream, session.Token);
                        if (!RemoteTrust.Authorized(directory, device, secret)) break;
                        var response = new JsonObject { ["id"] = request["id"]?.DeepClone() };
                        try { response["result"] = await Dispatcher.UIThread.InvokeAsync(() => handle(request)).ConfigureAwait(false); }
                        catch (Exception error) { response["error"] = error.Message; }
                        await RemoteWire.Write(stream, response, session.Token);
                    }
                }
                finally { session.Cancel(); await revocation; }
            }
            catch (Exception) { /* Client failure does not cancel host-owned agents. */ }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener?.Stop(); foreach (var client in clients.Keys) client.Dispose();
        await loop.ConfigureAwait(false);
    }
}
