using System.Text.Json.Nodes;
using Renci.SshNet;

namespace CodexManager;

public sealed record RemoteHost(string Name, string Address, int Port, string KeyPath, string Fingerprint)
{
    public override string ToString() => Name;
}
public sealed class RemoteOperationException(string message) : Exception(message);
public sealed class RemoteConnection : IDisposable
{
    private readonly SshClient client;
    private SshCommand? command;
    private StreamWriter? writer;
    private StreamReader? reader;
    private Task? execution;
    private readonly SemaphoreSlim gate = new(1);
    private readonly PrivateKeyFile key;
    public string? ObservedFingerprint { get; private set; }
    public RemoteConnection(RemoteHost host, string? passphrase = null)
    {
        key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(host.KeyPath) : new PrivateKeyFile(host.KeyPath, passphrase);
        client = new SshClient(host.Address, host.Port, "codex-manager", key);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.KeepAliveInterval = TimeSpan.FromSeconds(15);
        client.HostKeyReceived += (_, e) =>
        {
            ObservedFingerprint = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = string.Equals(host.Fingerprint, ObservedFingerprint, StringComparison.Ordinal);
        };
    }
    public async Task Connect(CancellationToken token)
    {
        await client.ConnectAsync(token);
        command = client.CreateCommand("codex-manager-rpc");
        execution = command.ExecuteAsync(token);
        writer = new StreamWriter(command.CreateInputStream(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        reader = new StreamReader(command.OutputStream);
    }
    public async Task<JsonNode?> Request(JsonObject request, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            request["id"] = Guid.NewGuid().ToString("N");
            await writer!.WriteLineAsync(request.ToJsonString().AsMemory(), token);
            var line = await reader!.ReadLineAsync(token) ?? throw new IOException("Remote host disconnected. Its agents continue running.");
            var result = JsonNode.Parse(line)!;
            if (result["id"]?.GetValue<string>() != request["id"]!.GetValue<string>()) throw new IOException("Unexpected remote response.");
            if (result["error"] is { } error) throw new RemoteOperationException(error.GetValue<string>());
            return result["result"]?.DeepClone();
        }
        catch (RemoteOperationException) { throw; }
        catch { Dispose(); throw; }
        finally { gate.Release(); }
    }
    public void Dispose() { client.Dispose(); command?.Dispose(); key.Dispose(); }
}
