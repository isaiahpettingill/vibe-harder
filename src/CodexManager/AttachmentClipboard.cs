using Avalonia.Input;
using Avalonia.Media.Imaging;
using System.Text;

namespace CodexManager;

public static class AttachmentClipboard
{
    private static readonly (string Mime, string Extension)[] ImageTypes = [("image/png", "png"), ("image/jpeg", "jpg"), ("image/webp", "webp"), ("image/gif", "gif")];
    public static async Task<Attachment?> Image(IAsyncDataTransfer data)
    {
        // Linux clipboard owners often advertise MIME bytes rather than a Bitmap.
        foreach (var (mime, extension) in ImageTypes)
            if (await data.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(mime)) is { Length: > 0 } bytes)
                return Bytes(bytes, extension);
        using var bitmap = await data.TryGetBitmapAsync();
        return bitmap is null ? null : Bitmap(bitmap);
    }
    public static Attachment? Image(IDataTransfer data)
    {
        foreach (var (mime, extension) in ImageTypes)
            if (data.TryGetValue(DataFormat.CreateBytesPlatformFormat(mime)) is { Length: > 0 } bytes)
                return Bytes(bytes, extension);
        using var bitmap = data.TryGetBitmap();
        return bitmap is null ? null : Bitmap(bitmap);
    }
    public const int LongPasteCharacters = 5000, LongPasteLines = 100;
    // Large pastes become a text file attachment with a marker in the draft, so the
    // composer never has to lay out (or the user scroll past) the whole block.
    public static Attachment? LongText(string text, IEnumerable<Attachment> existing)
    {
        if (text.Length < LongPasteCharacters && text.AsSpan().Count('\n') < LongPasteLines) return null;
        if (Encoding.UTF8.GetByteCount(text) > AttachmentFiles.MaximumBytes) throw new IOException("Paste less than 20 MB of text.");
        var used = existing.ToArray();
        var index = 1;
        while (used.Any(a => a.Name == $"Pasted text {index}.txt")) index++;
        var name = $"Pasted text {index}.txt";
        // The marker previews the paste so a draft with several of them stays readable.
        var preview = Preview(text);
        var reference = $"[\"{preview}\"]";
        for (var n = 2; used.Any(a => a.Reference == reference); n++) reference = $"[\"{preview}\" #{n}]";
        return new(name, "text/plain", text, "file:///" + Uri.EscapeDataString(name), reference);
    }
    private const int PreviewCharacters = 40;
    public static string Preview(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var words = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('"', '\'');
        if (words.Length <= PreviewCharacters) return words + (line.Length < text.Trim().Length ? "…" : "");
        var cut = words.LastIndexOf(' ', PreviewCharacters);
        return words[..(cut > PreviewCharacters / 2 ? cut : PreviewCharacters)].TrimEnd() + "…";
    }
    private static Attachment Bytes(byte[] bytes, string extension)
    {
        if (bytes.Length > AttachmentFiles.MaximumBytes) throw new IOException("Choose an image smaller than 20 MB.");
        var mime = ImageTypes.Single(t => t.Extension == extension).Mime;
        return new("Pasted image." + extension, mime, Convert.ToBase64String(bytes), Binary: true);
    }
    private static Attachment Bitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return Bytes(stream.ToArray(), "png");
    }
}
