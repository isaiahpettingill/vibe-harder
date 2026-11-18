using Avalonia.Platform.Storage;
using System.Text;

namespace CodexManager;

public static class AttachmentFiles
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars(), '/', '\\'];
    public static async Task<Attachment> Read(IStorageFile file, CancellationToken token = default)
    {
        await using var stream = await file.OpenReadAsync();
        var attachment = await Read(file.Name, stream, token);
        // Remember where a file picked on this machine lives, so the agent is given that path.
        return file.TryGetLocalPath() is { } local ? attachment with { SourcePath = new Uri(local).AbsoluteUri } : attachment;
    }
    public static Task<Attachment> Read(string name, Stream stream, CancellationToken token = default) =>
        OperatingSystem.IsBrowser() ? ReadCore(name, stream, token) : Task.Run(() => ReadCore(name, stream, token), token);

    private static async Task<Attachment> ReadCore(string name, Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;
        if (remaining > MaximumBytes) throw new IOException("Choose a file smaller than 20 MB.");
        using var data = new MemoryStream((int)remaining); var buffer = new byte[8192]; int count;
        var sinceYield = 0;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + count > MaximumBytes) throw new IOException("Choose a file smaller than 20 MB.");
            data.Write(buffer, 0, count);
            // WASM has no worker threads; give input and rendering a turn between chunks.
            if (OperatingSystem.IsBrowser() && (sinceYield += count) >= 256 * 1024)
            { sinceYield = 0; await Task.Delay(1, token); }
        }
        var mime = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "text/plain"
        };
        token.ThrowIfCancellationRequested();
        var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);
        if (mime == "text/plain" && !bytes.Contains((byte)0))
        {
            try { return new(name, mime, StrictUtf8.GetString(bytes)); }
            catch (DecoderFallbackException) { /* Not UTF-8: deliver the original bytes. */ }
        }
        return new(name, mime == "text/plain" ? "application/octet-stream" : mime, Convert.ToBase64String(bytes), Binary: true);
    }

    // An attachment can be opened in the system's app for its type (a long paste opens in the
    // text editor). Saved edits to a text copy replace the attachment's contents when it is sent.
    private sealed record OpenedCopy(string Path, DateTime Written);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Attachment, OpenedCopy> opened = new();
    // The file an attachment was read from, if it is a local file that still exists.
    public static string? LocalPath(Attachment attachment) =>
        Uri.TryCreate(attachment.SourcePath, UriKind.Absolute, out var uri) && uri.IsFile && Path.IsPathFullyQualified(uri.LocalPath) && File.Exists(uri.LocalPath) ? uri.LocalPath : null;
    public static string SafeName(string name)
    {
        name = name.Trim();
        if (name is "" or "." or "..") return "attachment";
        return name.AsSpan().IndexOfAny(InvalidNameChars) < 0 ? name : string.Create(name.Length, name, static (chars, value) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = InvalidNameChars.Contains(value[i]) ? '_' : value[i];
        });
    }
    public static byte[] Bytes(Attachment attachment) =>
        attachment.Binary || attachment.IsImage ? Convert.FromBase64String(attachment.Data) : new UTF8Encoding(false).GetBytes(attachment.Data);
    public static string WriteCopy(Attachment attachment)
    {
        if (opened.TryGetValue(attachment, out var existing) && File.Exists(existing.Path)) return existing.Path;
        var directory = Path.Combine(Path.GetTempPath(), "VibeHarder", "attachments", Guid.NewGuid().ToString("N")[..12]);
        var path = Path.Combine(directory, SafeName(attachment.Name));
        Directory.CreateDirectory(directory);
        try
        {
            if (attachment.Binary || attachment.IsImage) File.WriteAllBytes(path, Convert.FromBase64String(attachment.Data));
            else File.WriteAllText(path, attachment.Data);
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
        opened.AddOrUpdate(attachment, new(path, File.GetLastWriteTimeUtc(path)));
        return path;
    }
    public static Attachment WithSavedEdits(Attachment attachment)
    {
        if (attachment.Binary || attachment.IsImage || !opened.TryGetValue(attachment, out var copy) || !File.Exists(copy.Path)) return attachment;
        var written = File.GetLastWriteTimeUtc(copy.Path);
        if (written == copy.Written) return attachment;
        using var file = File.OpenRead(copy.Path);
        var length = file.Length;
        if (length > MaximumBytes) throw new IOException(attachment.Name + " is larger than 20 MB after editing.");
        var bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        Attachment updated;
        try { updated = attachment with { Data = StrictUtf8.GetString(bytes), SourcePath = new Uri(copy.Path).AbsoluteUri }; }
        catch (DecoderFallbackException error) { throw new IOException(attachment.Name + " is no longer UTF-8 text.", error); }
        opened.AddOrUpdate(updated, copy with { Written = written });
        return updated;
    }
}
