using System.Text.RegularExpressions;

namespace CodexManager;

public static class TerminalFileLinks
{
    private static readonly Regex Paths = new("(?:\"(?<path>(?:[A-Za-z]:[\\\\/]|/)[^\"\\r\\n]+)\"|'(?<path>(?:[A-Za-z]:[\\\\/]|/)[^'\\r\\n]+)'|(?<path>(?:[A-Za-z]:[\\\\/]|/)[^\\s<>\"'|]+))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Location = new(@"(?::\d+(?::\d+)?:?|\(\d+(?:,\d+)?\):)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Count brackets once, not once per discarded closer on a long wrapped link.
    internal static int UnquotedLength(ReadOnlySpan<char> value, string punctuation)
    {
        var length = value.TrimEnd(punctuation).Length;
        if (length == 0 || value[length - 1] is not (')' or ']' or '}')) return length;
        var parens = value.Count(')') - value.Count('(');
        var brackets = value.Count(']') - value.Count('[');
        var braces = value.Count('}') - value.Count('{');
        while (length > 0)
        {
            var c = value[length - 1];
            var discard = c switch
            {
                ')' => parens-- > 0,
                ']' => brackets-- > 0,
                '}' => braces-- > 0,
                _ => punctuation.Contains(c)
            };
            if (!discard) break;
            length--;
        }
        return length;
    }

    public static IEnumerable<(int Start, int Length, Uri Uri)> Find(string text, Workspace? workspace)
    {
        // Remote terminals never receive a local workspace, so cannot resolve files.
        if (workspace is null) yield break;
        foreach (Match match in Paths.Matches(text))
        {
            var group = match.Groups["path"];
            if (match.Index > 0 && !char.IsWhiteSpace(text[match.Index - 1]) && text[match.Index - 1] is not '(' and not '[' and not '=') continue;
            var quoted = group.Index != match.Index;
            var length = quoted ? group.Length : UnquotedLength(group.ValueSpan, ",;");
            var path = group.Value[..length];
            if (Location.Match(path) is { Success: true } location)
            {
                if (path.EndsWith(':')) length--; // Diagnostic separator, not part of the link.
                path = path[..location.Index];
            }
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
