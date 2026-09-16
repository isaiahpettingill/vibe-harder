using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

public static class FileLinks
{
    public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "VibeHarder-links");
    public static Func<string, Task>? OpenNativeFile { get; set; }
    public static string Resolve(string target, Workspace workspace)
    {
        var path = target.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        else path = Uri.UnescapeDataString(path);
        path = Regex.Replace(path, @"(?::\d+(?::\d+)?|#L\d+(?:C\d+)?(?:-L?\d+(?:C\d+)?)?)$", "");
        if (Regex.IsMatch(path, @"^/[A-Za-z]:[/\\]")) path = path[1..];
        if (workspace.IsWsl && !Regex.IsMatch(path, @"^[A-Za-z]:[/\\]") && !path.StartsWith(@"\\"))
        {
            if (!path.StartsWith('/')) path = workspace.Path.TrimEnd('/') + "/" + path;
            // DrvFS files already have a Windows path. Routing them through the
            // WSL network share can fail with access denied even when C:\ is readable.
            var drive = Regex.Match(path, @"^/mnt/([a-zA-Z])(?:/|$)");
            if (drive.Success)
                return drive.Groups[1].Value.ToUpperInvariant() + @":\" + path[drive.Length..].Replace('/', '\\');
            return @"\\wsl.localhost\" + workspace.Distro + path.Replace('/', '\\');
        }
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, workspace.Path);
    }

    public static void Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("File not found on this host.", path);
        var info = new ProcessStartInfo { UseShellExecute = true };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "explorer.exe";
            if (File.Exists(path)) info.ArgumentList.Add("/select,");
            info.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsMacOS()) { info.FileName = "open"; info.ArgumentList.Add("-R"); info.ArgumentList.Add(path); }
        else { info.FileName = "xdg-open"; info.ArgumentList.Add(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); }
        Process.Start(info)?.Dispose();
    }

    public static async Task<JsonNode> Read(JsonObject request, Workspace workspace)
    {
        var path = Resolve(request["path"]!.GetValue<string>(), workspace);
        if (Directory.Exists(path)) throw new IOException("This link points to a folder. Open it through Open workspace.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var offset = request["offset"]?.GetValue<long>() ?? 0;
        if (offset < 0 || offset > stream.Length) throw new IOException("Invalid file offset.");
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (request["stamp"] is { } expected && expected.GetValue<long>() != stamp) throw new IOException("File changed while downloading. Open the link again.");
        stream.Position = offset;
        var bytes = new byte[(int)Math.Min(256 * 1024, stream.Length - offset)];
        await stream.ReadExactlyAsync(bytes);
        return new JsonObject { ["name"] = Path.GetFileName(path), ["length"] = stream.Length, ["stamp"] = stamp, ["data"] = Convert.ToBase64String(bytes) };
    }

    public static async Task<string> Download(Func<JsonObject, Task<JsonNode?>> call, string chatId, string target, CancellationToken token)
    {
        var folder = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? path = null;
        try
        {
            long offset = 0; long? length = null, stamp = null;
            FileStream? output = null;
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = await call(new() { ["method"] = "file/read", ["chatId"] = chatId, ["path"] = target, ["offset"] = offset, ["stamp"] = stamp }) ?? throw new IOException("File download failed.");
                    var size = chunk["length"]!.GetValue<long>();
                    if (size < 0 || size > 512L * 1024 * 1024 || (length is not null && length != size)) throw new IOException("File is too large or changed during download.");
                    length = size; stamp ??= chunk["stamp"]!.GetValue<long>();
                    if (output is null)
                    {
                        var name = Path.GetFileName(chunk["name"]!.GetValue<string>().Replace('\\', '/'));
                        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new IOException("Invalid filename.");
                        path = Path.Combine(folder, name); output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    }
                    var bytes = Convert.FromBase64String(chunk["data"]!.GetValue<string>());
                    if (bytes.Length > 256 * 1024 || offset + bytes.Length > size || (bytes.Length == 0 && offset < size)) throw new IOException("Invalid file download response.");
                    await output.WriteAsync(bytes, token); offset += bytes.Length;
                } while (offset < length);
            }
            finally { if (output is not null) await output.DisposeAsync(); }
            return path!;
        }
        catch { if (path is not null) File.Delete(path); Directory.Delete(folder); throw; }
    }
}
