#if !MOBILE_CLIENT
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class WebAssets
{
    public const string ArchiveName = "VibeHarder-Web.zip";
    private const string Repository = "isaiahpettingill/vibe-harder";
    public static string Version => DesktopUpdater.Installation()?["version"]?.GetValue<string>()
        ?? typeof(WebAssets).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static DesktopRelease SelectRelease(JsonObject release, string version)
    {
        if (release["tag_name"]?.GetValue<string>() != "v" + version || release["draft"]?.GetValue<bool>() == true)
            throw new IOException("No matching web UI release is available.");
        var asset = (release["assets"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(a => a["name"]?.GetValue<string>() == ArchiveName)
            ?? throw new IOException("This release does not include the web UI yet.");
        var digest = asset["digest"]?.GetValue<string>() ?? "";
        var size = asset["size"]?.GetValue<long>() ?? 0;
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit) || size is <= 0 or > 512L * 1024 * 1024)
            throw new IOException("The web UI release has invalid checksum or size information.");
        var expected = $"https://github.com/{Repository}/releases/download/v{version}/{ArchiveName}";
        if (asset["browser_download_url"]?.GetValue<string>() != expected) throw new IOException("Unexpected web UI download address.");
        return new(System.Version.Parse(version), ArchiveName, new Uri(expected), digest[7..], size);
    }
    public static async Task<string> Ensure(string dataDirectory, Action<string> status, CancellationToken token)
    {
        // Local development can use a published bundle without waiting for a GitHub release.
        if (DesktopUpdater.Installation() is null && Environment.GetEnvironmentVariable("VIBE_HARDER_WEB_ASSETS") is { Length: > 0 } local)
        { Validate(local); return Path.GetFullPath(local); }
        var version = Version;
        var root = Path.Combine(dataDirectory, "web", version);
        var current = Path.Combine(root, "wwwroot");
        if (File.Exists(Path.Combine(current, ".complete")))
        {
            try { Validate(current); return current; }
            catch (IOException) { /* Repair an incomplete cache through the same verified download. */ }
        }
        status("Downloading web UI…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeHarder-Web/1.0");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/tags/v{version}", token);
        var release = SelectRelease(JsonNode.Parse(json)!.AsObject(), version);
        var archive = await DesktopUpdater.Download(release, token, http, Path.Combine(dataDirectory, "web-downloads"));
        status("Preparing web UI…");
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => Extract(archive, staging, token), token);
            await File.WriteAllTextAsync(Path.Combine(staging, ".complete"), version, token);
            // Never replace a running bundle with partially downloaded or extracted files.
            if (Directory.Exists(current)) Directory.Move(current, Path.Combine(root, "previous-" + Guid.NewGuid().ToString("N")));
            Directory.Move(staging, current);
            return current;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static void Extract(string archive, string destination, CancellationToken token = default)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        if (zip.Entries.Count > 20000) throw new IOException("Too many files in the web UI archive.");
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 1024L * 1024 * 1024) throw new IOException("Web UI archive is too large.");
            if (entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.Split('/').Any(p => p is "." or ".." || p.StartsWith('.')))
                throw new IOException("Invalid path in the web UI archive.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new IOException("Invalid path in the web UI archive.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
        Validate(destination);
    }
    private static void Validate(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "index.html")) || !File.Exists(Path.Combine(directory, "_framework", "dotnet.js")))
            throw new IOException("The web UI bundle is incomplete. Download it again.");
    }
}
#endif
