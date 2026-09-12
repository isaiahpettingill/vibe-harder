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
    public static async Task<Attachment> Read(string name, Stream stream, CancellationToken token = default)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > MaximumBytes) throw new IOException("Choose a file smaller than 20 MB.");
            data.Write(buffer, 0, count);
        }
        var mime = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
            ".pdf" => "application/pdf", ".zip" => "application/zip", _ => "text/plain"
        };
        var bytes = data.ToArray(); string text;
        var binary = mime != "text/plain";
        try { text = binary ? Convert.ToBase64String(bytes) : new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { binary = true; mime = "application/octet-stream"; text = Convert.ToBase64String(bytes); }
        return new(name, mime, text, "file:///" + Uri.EscapeDataString(name), Binary: binary);
    }
}
