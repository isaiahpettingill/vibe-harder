using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace CodexManager;

// SRP proves knowledge of the desktop code without sending it or exposing an
// offline guessing oracle. Bind the proof to the observed TLS certificate so
// a proxy cannot relay pairing and steal the resulting device credential.
public sealed class RemotePairingChallenge
{
    private readonly Srp6Server server = new();
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
    private readonly string publicValue;
    private bool used;
    public string Code { get; } = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
    public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(2);
    public RemotePairingChallenge(string fingerprint)
    {
        var group = Srp6StandardGroups.rfc5054_3072;
        var generator = new Srp6VerifierGenerator(); generator.Init(group, new Sha256Digest());
        var verifier = generator.GenerateVerifier(salt, Encoding.UTF8.GetBytes(fingerprint), Encoding.UTF8.GetBytes(Code));
        server.Init(group, verifier, new Sha256Digest(), new SecureRandom());
        publicValue = server.GenerateServerCredentials().ToString(16);
    }
    public JsonObject Offer() => new() { ["salt"] = Convert.ToBase64String(salt), ["public"] = publicValue, ["name"] = Environment.MachineName };
    public string Verify(JsonObject proof)
    {
        if (used || DateTimeOffset.UtcNow >= Expires) throw new IOException("Pairing expired. Request a new code.");
        used = true;
        server.CalculateSecret(Parse(proof, "public"));
        if (!server.VerifyClientEvidenceMessage(Parse(proof, "proof"))) throw new IOException("Incorrect pairing code. Request a new code and try again.");
        return server.CalculateServerEvidenceMessage().ToString(16);
    }
    internal static BigInteger Parse(JsonObject value, string key)
    {
        var text = value[key]?.GetValue<string>() ?? "";
        if (text.Length is 0 or > 768 || text.Any(c => !Uri.IsHexDigit(c))) throw new IOException("Invalid pairing proof.");
        return new BigInteger(text, 16);
    }
}

public sealed class RemotePairingSession : IDisposable
{
    private readonly RemoteConnection connection;
    private readonly JsonObject offer;
    private readonly RemoteHost host;
    private readonly DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(2);
    private bool completed;
    private RemotePairingSession(RemoteConnection connection, JsonObject offer, RemoteHost host)
    { this.connection = connection; this.offer = offer; this.host = host; }

    public static (string Host, int Port) ParseAddress(string address)
    {
        var input = address.Trim();
        if (OperatingSystem.IsBrowser())
        {
            if (!input.Contains("://")) input = "https://" + input;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || string.IsNullOrEmpty(endpoint.Host) || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new FormatException("Enter the computer's HTTPS web address.");
            return (endpoint.IdnHost.Trim('[', ']'), endpoint.Port);
        }
        if (input.Length == 0) throw new FormatException("Enter your computer's address, such as my-desktop or my-desktop.tailnet.ts.net.");
        if (!input.Contains("://") && input.Count(c => c == ':') > 1 && !input.StartsWith('[')) input = "[" + input + "]";
        if (!input.Contains("://")) input = "tcp://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "tcp" || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Port is 0 or > 65535)
            throw new FormatException("Enter a hostname or address, optionally followed by :port.");
        return (uri.IdnHost.Trim('[', ']'), uri.Port == -1 ? 2222 : uri.Port);
    }

    public static async Task<RemotePairingSession> Start(string address, string deviceName, CancellationToken token)
    {
        var endpoint = ParseAddress(address);
        var host = new RemoteHost(endpoint.Host, endpoint.Host, endpoint.Port, "", "");
        var connection = new RemoteConnection(host);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await connection.OpenForPairing(timeout.Token).ConfigureAwait(false);
            var offer = (await connection.Request(new() { ["method"] = "pairing/start", ["name"] = deviceName }, timeout.Token).ConfigureAwait(false))!.AsObject();
            return new(connection, offer, host with { Name = offer["name"]!.GetValue<string>(), Fingerprint = connection.ObservedFingerprint! });
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task<RemoteHost> Complete(string code, string credentialPath, CancellationToken token)
    {
        if (completed || DateTimeOffset.UtcNow >= expires) throw new IOException("Pairing expired. Request a new code.");
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new FormatException("Enter the six-digit number shown on your desktop.");
        completed = true;
        var client = new Srp6Client(); client.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest(), new SecureRandom());
        var salt = Convert.FromBase64String(offer["salt"]!.GetValue<string>());
        if (salt.Length != 16) throw new IOException("Invalid pairing challenge.");
        var publicValue = client.GenerateClientCredentials(salt, Encoding.UTF8.GetBytes(host.Fingerprint), Encoding.UTF8.GetBytes(code));
        client.CalculateSecret(RemotePairingChallenge.Parse(offer, "public"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var reply = (await connection.Request(new() { ["method"] = "pairing/finish", ["public"] = publicValue.ToString(16), ["proof"] = client.CalculateClientEvidenceMessage().ToString(16) }, timeout.Token).ConfigureAwait(false))!.AsObject();
        if (!client.VerifyServerEvidenceMessage(RemotePairingChallenge.Parse(reply, "proof"))) throw new IOException("Could not verify the computer. Request a new pairing code.");
        var credential = reply["credential"]!.AsObject();
        RemoteKey.WritePrivate(credentialPath, Encoding.UTF8.GetBytes(credential.ToJsonString()));
        return host with { KeyPath = credentialPath };
    }
    public void Dispose() => connection.Dispose();
}
