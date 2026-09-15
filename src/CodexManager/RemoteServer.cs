using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed class RemoteServer : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
    private TcpListener? listener;
    private readonly Task loop;
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
                        client.NoDelay = true;
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
                await new RemoteSessionProtocol(directory, handle, showPairing).Serve(
                    (token, maximum) => RemoteWire.Read(stream, token, maximum),
                    (value, token) => RemoteWire.Write(stream, value, token),
                    client.Dispose, RemoteTrust.Fingerprint(certificate), lifetime.Token);
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
