using Avalonia.Input;
using Avalonia.Media.Imaging;

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
