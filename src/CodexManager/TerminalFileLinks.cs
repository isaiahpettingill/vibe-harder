using System.Text.RegularExpressions;

namespace CodexManager;

public static class TerminalFileLinks
{
    private static readonly Regex Paths = new("(?:\"(?<path>(?:[A-Za-z]:[\\\\/]|/)[^\"\\r\\n]+)\"|'(?<path>(?:[A-Za-z]:[\\\\/]|/)[^'\\r\\n]+)'|(?<path>(?:[A-Za-z]:[\\\\/]|/)[^\\s<>\"'|]+))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Location = new(@":\d+(?::\d+)?$", RegexOptions.CultureInvariant);

    public static IEnumerable<(int Start, int Length, Uri Uri)> Find(string text, Workspace? workspace)
    {
        // Remote terminals never receive a local workspace, so cannot resolve files.
        if (workspace is null) yield break;
        foreach (Match match in Paths.Matches(text))
        {
            var group = match.Groups["path"];
            if (match.Index > 0 && !char.IsWhiteSpace(text[match.Index - 1]) && text[match.Index - 1] is not '(' and not '[' and not '=') continue;
            var path = group.Value.TrimEnd(',', ';', ')', ']', '}');
            var length = path.Length;
            path = Location.Replace(path, "");
            if (workspace.IsWsl)
            {
                if (!path.StartsWith('/') || path.StartsWith("//")) continue;
                path = @"\\wsl.localhost\" + workspace.Distro + path.Replace('/', '\\');
            }
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                yield return (group.Index, length, uri);
        }
    }
}
