using Avalonia.Platform.Storage;
using System.Text;

namespace CodexManager;

public static class AttachmentFiles
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    public static async Task<Attachment> Read(IStorageFile file, CancellationToken token = default)
    {
        await using var stream = await file.OpenReadAsync();
        return await Read(file.Name, stream, token);
    }
    public static Task<Attachment> Read(string name, Stream stream, CancellationToken token = default) =>
        OperatingSystem.IsBrowser() ? ReadCore(name, stream, token) : Task.Run(() => ReadCore(name, stream, token), token);

    private static async Task<Attachment> ReadCore(string name, Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var data = new MemoryStream(); var buffer = new byte[8192]; int count;
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
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
            ".pdf" => "application/pdf", ".zip" => "application/zip", _ => "text/plain"
        };
        token.ThrowIfCancellationRequested();
        var bytes = data.ToArray(); string text;
        var binary = mime != "text/plain";
        try { text = binary ? Convert.ToBase64String(bytes) : new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { binary = true; mime = "application/octet-stream"; text = Convert.ToBase64String(bytes); }
        return new(name, mime, text, "file:///" + Uri.EscapeDataString(name), Binary: binary);
    }

    // An attachment can be opened in the system's app for its type (a long paste opens in the
    // text editor). Saved edits to a text copy replace the attachment's contents when it is sent.
    private sealed record OpenedCopy(string Path, DateTime Written);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Attachment, OpenedCopy> opened = new();
    public static string WriteCopy(Attachment attachment)
    {
        if (opened.TryGetValue(attachment, out var existing) && File.Exists(existing.Path)) return existing.Path;
        var directory = Path.Combine(Path.GetTempPath(), "VibeHarder", "attachments", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var name = string.Concat(attachment.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var path = Path.Combine(directory, name.Length > 0 ? name : "attachment");
        if (attachment.Binary || attachment.IsImage) File.WriteAllBytes(path, Convert.FromBase64String(attachment.Data));
        else File.WriteAllText(path, attachment.Data, new UTF8Encoding(false));
        opened.AddOrUpdate(attachment, new(path, File.GetLastWriteTimeUtc(path)));
        return path;
    }
    public static Attachment WithSavedEdits(Attachment attachment)
    {
        if (attachment.Binary || attachment.IsImage || !opened.TryGetValue(attachment, out var copy) || !File.Exists(copy.Path)) return attachment;
        var written = File.GetLastWriteTimeUtc(copy.Path);
        if (written == copy.Written) return attachment;
        var bytes = File.ReadAllBytes(copy.Path);
        if (bytes.Length > MaximumBytes) throw new IOException(attachment.Name + " is larger than 20 MB after editing.");
        var updated = attachment with { Data = new UTF8Encoding(false).GetString(bytes) };
        opened.AddOrUpdate(updated, copy with { Written = written });
        return updated;
    }
}
