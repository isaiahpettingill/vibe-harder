using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class RemoteTrust
{
    private static readonly object Sync = new();
    public static X509Certificate2 Certificate(string directory)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, "host.pfx");
            if (!File.Exists(path))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest("CN=Vibe Harder", key, HashAlgorithmName.SHA256);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
                RemoteKey.WritePrivate(path, certificate.Export(X509ContentType.Pfx));
            }
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }
    }
    public static string Fingerprint(X509Certificate2 certificate) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=');
    private static JsonObject Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new();
    private static void Write(string path, JsonObject data) => RemoteKey.WritePrivate(path, Encoding.UTF8.GetBytes(data.ToJsonString()));
    public static string Invite(string directory, string address, int port, string name)
    {
        if (string.IsNullOrWhiteSpace(address) || address is "0.0.0.0" or "::") throw new ArgumentException("Enter the host's LAN, VPN, or tailnet address for the connection code.");
        lock (Sync)
        {
            using var certificate = Certificate(directory); var secret = RemoteKey.NewSecret();
            Write(Path.Combine(directory, "pairing.json"), new() { ["hash"] = RemoteKey.Hash(secret), ["expires"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() });
            var invite = new JsonObject { ["version"] = 1, ["name"] = name, ["address"] = address, ["port"] = port, ["fingerprint"] = Fingerprint(certificate), ["code"] = secret };
            return "vibeharder://pair/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(invite.ToJsonString()));
        }
    }
    public static JsonObject Pair(string directory, string code, string name)
    {
        lock (Sync)
        {
            var path = Path.Combine(directory, "pairing.json"); var invite = Read(path);
            if (invite["expires"]?.GetValue<long>() is not { } expires || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || invite["hash"]?.GetValue<string>() != RemoteKey.Hash(code)) throw new IOException("Connection code expired or was already used. Create a new code on the host.");
            var id = Guid.NewGuid().ToString("N"); var secret = RemoteKey.NewSecret();
            var devices = Read(Path.Combine(directory, "devices.json"));
            devices[id] = new JsonObject { ["name"] = name[..Math.Min(100, name.Length)], ["hash"] = RemoteKey.Hash(secret), ["paired"] = DateTimeOffset.UtcNow.ToString("O") };
            Write(path, new()); Write(Path.Combine(directory, "devices.json"), devices);
            return new JsonObject { ["device"] = id, ["token"] = secret };
        }
    }
    public static bool Authorized(string directory, string id, string token)
    {
        lock (Sync)
        {
            var expected = Read(Path.Combine(directory, "devices.json"))[id]?["hash"]?.GetValue<string>();
            return expected is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(RemoteKey.Hash(token)));
        }
    }
    public static JsonObject Devices(string directory) { lock (Sync) return Read(Path.Combine(directory, "devices.json")); }
    public static void Revoke(string directory, string id) { lock (Sync) { var path = Path.Combine(directory, "devices.json"); var devices = Read(path); devices.Remove(id); Write(path, devices); } }
}
