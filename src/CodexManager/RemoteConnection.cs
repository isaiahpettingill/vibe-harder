using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Buffers.Binary;

namespace CodexManager;

public sealed record RemoteHost(string Name, string Address, int Port, string KeyPath, string Fingerprint)
{
    public override string ToString() => Name;
}
public sealed class RemoteOperationException(string message) : Exception(message);
public sealed class RemoteRequestBusyException : IOException
{
    public RemoteRequestBusyException() : base("Waiting for another host request to finish.") { }
}

public sealed class RemoteConnection : IDisposable
{
    private readonly RemoteHost host;
    private readonly TcpClient client = new();
    private SslStream? stream;
    private readonly SemaphoreSlim gate = new(1);
    private int disposed;
    public string? ObservedFingerprint { get; private set; }
    public RemoteConnection(RemoteHost host) => this.host = host;
    internal Task OpenForPairing(CancellationToken token) => Open(token, pairing: true);
    private async Task Open(CancellationToken token, bool pairing = false)
    {
        await client.ConnectAsync(host.Address, host.Port, token).ConfigureAwait(false);
        stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            ObservedFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('=');
            return pairing || string.Equals(host.Fingerprint, ObservedFingerprint, StringComparison.Ordinal);
        });
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host.Address, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token).ConfigureAwait(false);
    }
    public async Task Connect(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var abort = timeout.Token.Register(Dispose);
        await Open(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        var credential = JsonNode.Parse(await File.ReadAllTextAsync(host.KeyPath, timeout.Token).ConfigureAwait(false))?.AsObject() ?? throw new IOException("Pair this host again in Remote settings.");
        credential["method"] = "auth";
        await Request(credential, timeout.Token).ConfigureAwait(false);
    }
    public static async Task<RemoteHost> Pair(string code, string credentialPath, string deviceName, CancellationToken token)
    {
        var invite = RemoteKey.ParseCode(code);
        var host = new RemoteHost(invite["name"]!.GetValue<string>(), invite["address"]!.GetValue<string>(), invite["port"]!.GetValue<int>(), credentialPath, invite["fingerprint"]!.GetValue<string>());
        using var client = new RemoteConnection(host);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await client.Open(timeout.Token).ConfigureAwait(false);
        var credential = await client.Request(new() { ["method"] = "pair", ["code"] = invite["code"]!.DeepClone(), ["name"] = deviceName }, timeout.Token).ConfigureAwait(false);
        await Task.Run(() => RemoteKey.WritePrivate(credentialPath, Encoding.UTF8.GetBytes(credential!.ToJsonString())), timeout.Token).ConfigureAwait(false);
        return host;
    }
    public async Task<JsonNode?> Request(JsonObject request, CancellationToken token)
    {
        // A queued poll expiring does not mean the socket is broken. In particular,
        // a slow send/import may legitimately hold this connection for 90 seconds.
        try { await gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new RemoteRequestBusyException(); }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var abort = token.Register(Dispose);
            request["id"] = Guid.NewGuid().ToString("N");
            await RemoteWire.Write(stream!, request, token).WaitAsync(token).ConfigureAwait(false);
            var response = await RemoteWire.Read(stream!, token).WaitAsync(token).ConfigureAwait(false);
            if (response["id"]?.GetValue<string>() != request["id"]!.GetValue<string>()) throw new IOException("Unexpected remote response.");
            if (response["error"] is { } error) throw new RemoteOperationException(error.GetValue<string>());
            var result = response["result"]; response.Remove("result"); return result;
        }
        catch (RemoteOperationException) { throw; }
        catch { Dispose(); throw; }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Close the socket first to interrupt a TLS read, including on Android resume.
        client.Dispose(); stream?.Dispose();
    }
}

public static class RemoteWire
{
    public const int MaximumFrame = 32 * 1024 * 1024;
    public static async Task<JsonObject> Read(Stream stream, CancellationToken token, int maximum = MaximumFrame)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > maximum) throw new IOException("Invalid remote message size.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonNode.Parse(bytes)?.AsObject() ?? throw new IOException("Invalid remote message.");
    }
    public static async Task Write(Stream stream, JsonNode value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        if (bytes.Length > MaximumFrame) throw new IOException("Remote message is too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }
}
