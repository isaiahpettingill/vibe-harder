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
        if (text.Length < LongPasteCharacters)
        {
            var breaks = 0;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\r' || text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) breaks++;
            if (breaks < LongPasteLines) return null;
        }
        if (Encoding.UTF8.GetByteCount(text) > AttachmentFiles.MaximumBytes) throw new IOException("Paste less than 20 MB of text.");
        var names = new HashSet<string>(); var references = new HashSet<string>();
        foreach (var item in existing) { names.Add(item.Name); if (item.Reference is { } marker) references.Add(marker); }
        var index = 1;
        while (names.Contains($"Pasted text {index}.txt")) index++;
        var name = $"Pasted text {index}.txt";
        // The marker previews the paste so a draft with several of them stays readable.
        var preview = Preview(text);
        var reference = $"[\"{preview}\"]";
        for (var n = 2; references.Contains(reference); n++) reference = $"[\"{preview}\" #{n}]";
        return new(name, "text/plain", text, Reference: reference);
    }
    private const int PreviewCharacters = 40;
    public static string Preview(string text)
    {
        var content = text.AsSpan().Trim();
        var newline = content.IndexOfAny('\r', '\n');
        var line = newline < 0 ? content : content[..newline].TrimEnd();
        Span<char> prefix = stackalloc char[PreviewCharacters + 1];
        var count = 0; var space = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space) { prefix[count++] = ' '; space = false; }
            if (count < prefix.Length) prefix[count++] = ch == '"' ? '\'' : ch;
            if (count == prefix.Length) break;
        }
        if (count <= PreviewCharacters) return prefix[..count].ToString() + (newline >= 0 ? "…" : "");
        var cut = prefix.LastIndexOf(' ');
        if (cut <= PreviewCharacters / 2) cut = PreviewCharacters;
        if (char.IsHighSurrogate(prefix[cut - 1])) cut--;
        return prefix[..cut].ToString() + "…";
    }
    private static Attachment Bytes(ReadOnlySpan<byte> bytes, string extension)
    {
        if (bytes.Length > AttachmentFiles.MaximumBytes) throw new IOException("Choose an image smaller than 20 MB.");
        var mime = ImageTypes.Single(t => t.Extension == extension).Mime;
        return new("Pasted image." + extension, mime, Convert.ToBase64String(bytes), Binary: true);
    }
    private static Attachment Bitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return Bytes(stream.GetBuffer().AsSpan(0, (int)stream.Length), "png");
    }
}
