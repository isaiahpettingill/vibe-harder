using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class RemoteTrust
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> PairingWindows = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static void OpenPairing(string directory) { lock (Sync) PairingWindows[Path.GetFullPath(directory)] = DateTimeOffset.UtcNow.AddMinutes(2); }
    public static bool PairingEnabled(string directory) { lock (Sync) return PairingWindows.GetValueOrDefault(Path.GetFullPath(directory)) > DateTimeOffset.UtcNow; }
    private sealed record DeviceCache(JsonObject Devices, DateTime Stamp, long Length, DateTimeOffset Checked);
    private static readonly Dictionary<string, DeviceCache> DeviceCaches = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, OperatingSystem.IsLinux() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.UserKeySet);
        }
    }
    public static string Fingerprint(X509Certificate2 certificate) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=');
    private static JsonObject Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new();
    private static void Write(string path, JsonObject data)
    {
        RemoteKey.WritePrivate(path, Encoding.UTF8.GetBytes(data.ToJsonString()));
        DeviceCaches.Remove(Path.GetFullPath(path));
    }
    private static JsonObject CachedDevices(string directory)
    {
        var path = Path.GetFullPath(Path.Combine(directory, "devices.json"));
        var now = DateTimeOffset.UtcNow;
        if (DeviceCaches.TryGetValue(path, out var cached) && now - cached.Checked < TimeSpan.FromSeconds(1)) return cached.Devices;
        var info = new FileInfo(path);
        if (cached is not null && info.Exists && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
        { DeviceCaches[path] = cached with { Checked = now }; return cached.Devices; }
        var devices = Read(path);
        DeviceCaches[path] = new(devices, info.LastWriteTimeUtc, info.Exists ? info.Length : 0, now);
        return devices;
    }
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
            Write(path, new());
            return AddDevice(directory, name);
        }
    }
    internal static JsonObject AddDevice(string directory, string name)
    {
        lock (Sync)
        {
            var id = Guid.NewGuid().ToString("N"); var secret = RemoteKey.NewSecret();
            var devices = Read(Path.Combine(directory, "devices.json"));
            devices[id] = new JsonObject { ["name"] = name[..Math.Min(100, name.Length)], ["hash"] = RemoteKey.Hash(secret), ["paired"] = DateTimeOffset.UtcNow.ToString("O") };
            Write(Path.Combine(directory, "devices.json"), devices);
            return new JsonObject { ["device"] = id, ["token"] = secret };
        }
    }
    public static bool Authorized(string directory, string id, string token)
    {
        lock (Sync)
        {
            try
            {
                var expected = CachedDevices(directory)[id]?["hash"]?.GetValue<string>();
                return expected is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(RemoteKey.Hash(token)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            { DeviceCaches.Remove(Path.GetFullPath(Path.Combine(directory, "devices.json"))); return false; }
        }
    }
    public static JsonObject Devices(string directory) { lock (Sync) return Read(Path.Combine(directory, "devices.json")); }
    // A paired device's UnifiedPush endpoint and keys; null removes them.
    public static void SetPush(string directory, string id, JsonObject? push)
    {
        lock (Sync)
        {
            var path = Path.Combine(directory, "devices.json"); var devices = Read(path);
            if (devices[id] is not JsonObject device) return;
            if (push is null) device.Remove("push"); else device["push"] = push;
            Write(path, devices); DeviceCaches.Remove(Path.GetFullPath(path));
        }
    }
    public static IReadOnlyList<(string Device, JsonObject Push)> PushTargets(string directory)
    {
        lock (Sync)
            return Read(Path.Combine(directory, "devices.json")).Where(d => d.Value?["push"] is JsonObject).Select(d => (d.Key, (JsonObject)d.Value!["push"]!.DeepClone())).ToArray();
    }
    // The computer's VAPID identity for push servers, created once.
    public static WebPush.KeyPair VapidKey(string directory)
    {
        lock (Sync)
        {
            var path = Path.Combine(directory, "vapid.json");
            var saved = Read(path);
            if (saved["public"]?.GetValue<string>() is { } publicKey && saved["private"]?.GetValue<string>() is { } privateKey)
                return new(WebPush.FromBase64Url(publicKey), WebPush.FromBase64Url(privateKey));
            var key = WebPush.Generate();
            Directory.CreateDirectory(directory);
            RemoteKey.WritePrivate(path, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["public"] = WebPush.Base64Url(key.PublicKey), ["private"] = WebPush.Base64Url(key.PrivateKey) }.ToJsonString()));
            return key;
        }
    }
    public static void Revoke(string directory, string id) { lock (Sync) { var path = Path.Combine(directory, "devices.json"); var devices = Read(path); devices.Remove(id); Write(path, devices); } }
}
