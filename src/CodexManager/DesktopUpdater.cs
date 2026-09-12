using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed record DesktopRelease(Version Version, string Name, Uri Url, string Sha256, long Size);

public static class DesktopUpdater
{
    private const string Repository = "isaiahpettingill/vibe-harder";
    private static readonly HttpClient Http = CreateClient();
    public static HashSet<string> ConsumeResumeChats(Store store, bool updated)
    {
        var chats = updated ? (store.Setting("updateResume") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : [];
        store.Setting("updateResume", "");
        return chats;
    }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VibeHarder-Updater/1.0");
        return client;
    }

    public static JsonObject? Installation()
    {
        if (OperatingSystem.IsAndroid()) return null;
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update.json"))) as JsonObject; }
        catch { return null; } // Development builds do not update themselves.
    }

    public static DesktopRelease? SelectRelease(JsonObject release, Version installed, string runtime, string mode)
    {
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true ||
            !Version.TryParse(release["tag_name"]?.GetValue<string>()?.TrimStart('v'), out var version) || version <= installed) return null;
        if (mode is not ("aot" or "bundled" or "framework")) return null;
        var suffix = runtime.StartsWith("win-", StringComparison.Ordinal) ? "-Setup.exe" : runtime.StartsWith("linux-", StringComparison.Ordinal) ? ".tar.gz" : runtime.StartsWith("osx-", StringComparison.Ordinal) ? ".zip" : null;
        if (suffix is null) return null;
        var name = $"VibeHarder-{version}-{runtime}-{mode}{suffix}";
        var asset = (release["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.GetValue<string>() == name);
        if (asset is null) return null;
        var digest = asset["digest"]?.GetValue<string>();
        var size = asset["size"]?.GetValue<long>() ?? 0;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit) || size <= 0 || size > 1024L * 1024 * 1024) return null;
        if (!Uri.TryCreate(asset["browser_download_url"]?.GetValue<string>(), UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal)) return null;
        return new(version, name, url, digest[7..], size);
    }

    public static async Task<DesktopRelease?> Check(JsonObject installation, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        return SelectRelease(JsonNode.Parse(json)!.AsObject(), Version.Parse(installation["version"]!.GetValue<string>()), installation["runtime"]!.GetValue<string>(), installation["mode"]!.GetValue<string>());
    }

    public static async Task<string> Download(DesktopRelease release, CancellationToken cancellation, HttpClient? client = null, string? dataDirectory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var directory = Path.Combine(dataDirectory ?? Store.DataDirectory, "updates", release.Version.ToString());
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, release.Name);
        var partial = target + ".partial";
        try
        {
            using var response = await (client ?? Http).GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(partial))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    length += count;
                    if (length > release.Size) throw new IOException("Update exceeds its published size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                if (length != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update checksum verification failed.");
            }
            File.Move(partial, target, true);
            return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string UnixInstallScript(int processId, string prepared, string destination, string executable, string log)
    {
        var backup = prepared + ".previous";
        return $"#!/bin/sh\nwhile kill -0 {processId} 2>/dev/null; do sleep 1; done\nexec >>{ShellQuote(log)} 2>&1\nif mv {ShellQuote(destination)} {ShellQuote(backup)}; then\n  if mv {ShellQuote(prepared)} {ShellQuote(destination)}; then\n    rm -rf -- {ShellQuote(backup)}\n  else\n    mv {ShellQuote(backup)} {ShellQuote(destination)}\n  fi\nfi\nexec {ShellQuote(executable)} --updated\n";
    }

    // The helper waits for the normal shutdown to flush sessions and release application files.
    public static void InstallAfterExit(string package)
    {
        var target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var log = Path.Combine(Path.GetDirectoryName(package)!, "install.log");
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            var executable = Path.Combine(target, "VibeHarder.exe");
            var script = $"$ErrorActionPreference='Stop'\nWait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue\ntry {{\n$p = Start-Process -FilePath {PowerShellQuote(package)} -ArgumentList {PowerShellQuote("/S /D=" + target)} -Wait -PassThru\nif ($p.ExitCode -ne 0) {{ throw 'Update installer failed' }}\n}} catch {{ $_ | Out-File -LiteralPath {PowerShellQuote(log)} }}\nStart-Process -FilePath {PowerShellQuote(executable)} -ArgumentList '--updated'\n";
            start = new("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        else
        {
            var staging = Path.Combine(Path.GetDirectoryName(package)!, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var unpack = OperatingSystem.IsMacOS() ? new ProcessStartInfo("ditto") : new ProcessStartInfo("tar");
            foreach (var arg in OperatingSystem.IsMacOS() ? new[] { "-x", "-k", package, staging } : new[] { "-xzf", package, "-C", staging }) unpack.ArgumentList.Add(arg);
            unpack.UseShellExecute = false;
            using (var extraction = Process.Start(unpack)!) { extraction.WaitForExit(); if (extraction.ExitCode != 0) throw new IOException("Cannot extract update."); }
            var source = OperatingSystem.IsMacOS() ? Path.Combine(staging, "Vibe Harder.app") : staging;
            var destination = OperatingSystem.IsMacOS() ? Directory.GetParent(target)!.Parent!.FullName : target;
            var executable = Path.Combine(target, "VibeHarder");
            if (!File.Exists(Path.Combine(source, OperatingSystem.IsMacOS() ? "Contents/MacOS/VibeHarder" : "VibeHarder"))) throw new IOException("Update is missing the application.");
            // Stage beside the installation so replacement uses same-filesystem renames.
            // System-owned installs must be updated by their owner.
            var prepared = destination + ".update-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(prepared);
            var copy = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "ditto" : "cp") { UseShellExecute = false };
            foreach (var arg in OperatingSystem.IsMacOS() ? new[] { source, prepared } : new[] { "-a", source + "/.", prepared + "/" }) copy.ArgumentList.Add(arg);
            using (var copying = Process.Start(copy)!) { copying.WaitForExit(); if (copying.ExitCode != 0) throw new IOException("Cannot stage update beside the installation."); }
            var script = Path.Combine(Path.GetDirectoryName(package)!, "install.sh");
            File.WriteAllText(script, UnixInstallScript(Environment.ProcessId, prepared, destination, executable, log));
            start = new("/bin/sh") { UseShellExecute = false }; start.ArgumentList.Add(script);
        }
        using var helper = Process.Start(start) ?? throw new IOException("Cannot start update installer.");
    }
}
