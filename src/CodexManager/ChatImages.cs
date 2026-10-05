using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace CodexManager;

// Loads images that models link in chat. Every re-render while a reply streams
// rebuilds the document, so loads are shared and the decoded bitmaps cached.
public static partial class ChatImages
{
    private const long MaxBytes = 25L * 1024 * 1024;
    private const int MaxWidth = 1600;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, Task<Bitmap>> Cache = [];
    private static readonly LinkedList<string> Order = [];

    [GeneratedRegex(@"\.(?:png|jpe?g|gif|webp|bmp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageExtension();
    [GeneratedRegex(@"^data:image/(?:png|jpeg|gif|webp|bmp);base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataImage();

    // A plain link is shown as an image only when its target names an image file.
    public static bool IsImageLink(string url)
    {
        if (DataImage().IsMatch(url)) return true;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.AbsolutePath : url.Split('?', '#')[0];
        return ImageExtension().IsMatch(path.TrimEnd('/'));
    }
    // Stops a remote image download as soon as the host reports a file too large to show.
    public static void CheckDownload(long received, long total)
    {
        if (total > MaxBytes) throw new IOException("The image is too large to show.");
    }
    public static bool IsWeb(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // Files resolve through the caller: a local path, a WSL share, or a download from a remote host.
    public static Task<Bitmap> Load(string url, string scope, Func<string, CancellationToken, Task<string>> resolveFile)
    {
        var key = (IsWeb(url) || DataImage().IsMatch(url) ? "" : scope + "|") + url;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached) && !cached.IsFaulted && !cached.IsCanceled)
            { Order.Remove(key); Order.AddFirst(key); return cached; }
            var load = Task.Run(async () => Decode(await Read(url, resolveFile)));
            Cache[key] = load; Order.Remove(key); Order.AddFirst(key);
            while (Order.Count > 64) { Cache.Remove(Order.Last!.Value); Order.RemoveLast(); }
            return load;
        }
    }
    private static async Task<byte[]> Read(string url, Func<string, CancellationToken, Task<string>> resolveFile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (DataImage().Match(url) is { Success: true } data)
        {
            var bytes = Convert.FromBase64String(url[data.Length..]);
            return bytes.Length <= MaxBytes ? bytes : throw new IOException("The image is too large to show.");
        }
        if (IsWeb(url))
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The link is not an image.");
            if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("The image is too large to show.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadCapped(stream, timeout.Token);
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1) throw new IOException("Unsupported image link.");
        var path = await resolveFile(url, timeout.Token);
        if (new FileInfo(path).Length > MaxBytes) throw new IOException("The image is too large to show.");
        await using var file = File.OpenRead(path);
        return await ReadCapped(file, timeout.Token);
    }
    private static async Task<byte[]> ReadCapped(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new IOException("The image is too large to show.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    private static Bitmap Decode(byte[] bytes)
    {
        try
        {
            var bitmap = new Bitmap(new MemoryStream(bytes));
            if (bitmap.PixelSize.Width <= MaxWidth) return bitmap;
            // Huge screenshots are shown scaled down anyway; keep them small in memory.
            bitmap.Dispose(); return Bitmap.DecodeToWidth(new MemoryStream(bytes), MaxWidth);
        }
        catch (Exception error) when (error is not IOException) { throw new IOException("The file is not a supported image.", error); }
    }
}
