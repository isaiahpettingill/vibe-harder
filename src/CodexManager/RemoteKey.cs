using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class RemoteKey
{
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static void WritePrivate(string path, byte[] content)
    {
        if (OperatingSystem.IsBrowser()) { BrowserPlatform.Write("credential:" + path, Encoding.UTF8.GetString(content)); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            file.Write(content); file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    public static JsonObject ParseCode(string code)
    {
        const string prefix = "vibeharder://pair/";
        if (!code.Trim().StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Paste a connection code from the host's Remote settings.");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim()[prefix.Length..]));
        var invite = JsonNode.Parse(json)!.AsObject();
        if (invite["version"]?.GetValue<int>() != 1 || invite["port"]!.GetValue<int>() is < 1 or > 65535) throw new FormatException("Unsupported connection code.");
        return invite;
    }
}
