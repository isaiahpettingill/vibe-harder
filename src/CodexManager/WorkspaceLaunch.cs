namespace CodexManager;

public static class WorkspaceLaunch
{
    public static string? Parse(string[] args)
    {
        if (args.Length == 0 || args.Length == 1 && args[0] is "--startup" or "--updated") return null;
        var path = args.Length == 2 && args[0] == "--workspace" ? args[1]
            : args.Length == 1 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0]
            : throw new ArgumentException("Usage: vibe-harder [directory]");
        return Normalize(path);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a directory to open.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Directory does not exist: " + full);
        return full;
    }

    public static bool SameLocalPath(string left, string right)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
