using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed class AcpFileSystem(Workspace workspace, Func<string?> sessionId, Func<string, bool>? knownChild = null)
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly SemaphoreSlim access = new(1);
    private readonly Dictionary<string, byte[]> snapshots = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private string PathFor(JsonElement request)
    {
        var requestedSession = request.GetProperty("sessionId").GetString();
        if (string.IsNullOrWhiteSpace(requestedSession) || sessionId() is { } expected && requestedSession != expected && knownChild?.Invoke(requestedSession) != true) throw new ArgumentException("Unknown session.");
        var path = request.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new ArgumentException("An absolute file path is required.");
        if (!workspace.IsWsl)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute file path is required.");
            return Path.GetFullPath(path);
        }
        if (!OperatingSystem.IsWindows() || !path.StartsWith('/') || path.Contains('\\')) throw new ArgumentException("An absolute Linux file path is required for WSL.");
        List<string> parts = [];
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        return @"\\wsl.localhost\" + workspace.Distro + "\\" + string.Join('\\', parts);
    }
    public async Task<JsonObject> Read(JsonElement request, CancellationToken token)
    {
        var path = PathFor(request);
        var first = request.TryGetProperty("line", out var line) ? line.GetInt32() : 1;
        var count = request.TryGetProperty("limit", out var limit) ? limit.GetInt32() : int.MaxValue;
        if (first < 1 || count < 0) throw new ArgumentException("line must be at least 1 and limit must be nonnegative.");
        await access.WaitAsync(token);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var bytes = await ReadBytes(stream, token);
            snapshots[path] = SHA256.HashData(bytes);
            var text = Utf8.GetString(bytes);
            if (text.StartsWith('\ufeff')) text = text[1..];
            if (first != 1 || count != int.MaxValue)
            {
                var start = 0;
                for (var i = 1; i < first && start < text.Length; i++) { var newline = text.IndexOf('\n', start); start = newline < 0 ? text.Length : newline + 1; }
                var end = start;
                for (var i = 0; i < count && end < text.Length; i++) { var newline = text.IndexOf('\n', end); end = newline < 0 ? text.Length : newline + 1; }
                text = text[start..end];
            }
            return new JsonObject { ["content"] = text };
        }
        finally { access.Release(); }
    }
    public async Task<JsonObject> Write(JsonElement request, CancellationToken token)
    {
        var path = PathFor(request);
        var content = request.GetProperty("content").GetString() ?? throw new ArgumentException("content must be text.");
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > MaxBytes) throw new IOException("ACP text files are limited to 16 MiB.");
        await access.WaitAsync(token);
        try
        {
            var previouslyRead = snapshots.TryGetValue(path, out var previous);
            if (!previouslyRead) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, previouslyRead ? FileMode.Open : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, true);
            if (previouslyRead && !SHA256.HashData(await ReadBytes(stream, token)).AsSpan().SequenceEqual(previous))
                throw new IOException("File changed since the agent read it. Read it again before editing.");
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            await stream.WriteAsync(bytes, token); stream.SetLength(bytes.Length); await stream.FlushAsync(token);
            snapshots[path] = SHA256.HashData(bytes);
            return new JsonObject();
        }
        finally { access.Release(); }
    }
    private static async Task<byte[]> ReadBytes(FileStream stream, CancellationToken token)
    {
        if (stream.Length > MaxBytes) throw new IOException("ACP text files are limited to 16 MiB.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }
}
