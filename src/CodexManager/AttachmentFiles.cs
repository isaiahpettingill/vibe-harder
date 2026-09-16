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
}
