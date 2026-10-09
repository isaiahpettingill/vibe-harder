using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace CodexManager;

// Files dropped from another app. Explorer, Finder and most file managers hand over a file list;
// some file managers and Linux backends offer only a text/uri-list or plain paths, read here as well.
public static class DroppedFiles
{
    private static readonly DataFormat<string> UriList = DataFormat.CreateStringPlatformFormat("text/uri-list");

    public static async Task<IStorageItem[]> Read(IDataTransfer data, IStorageProvider storage)
    {
        if (data.TryGetFiles()?.ToArray() is { Length: > 0 } files) return files;
        var text = (data.Contains(UriList) ? data.TryGetValue(UriList) : null) ?? data.TryGetText();
        var found = new List<IStorageItem>();
        foreach (var path in Paths(text))
            if (await storage.TryGetFileFromPathAsync(path) is { } file) found.Add(file);
        return found.ToArray();
    }

    // Existing local files named one per line, as file:// URIs or full paths; comments start with #.
    public static IEnumerable<string> Paths(string? text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var path = Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Path.IsPathFullyQualified(line) ? line : null;
            if (path is not null && File.Exists(path)) yield return path;
        }
    }
}
