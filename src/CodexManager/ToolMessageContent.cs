namespace CodexManager;

internal static class ToolMessageContent
{
    // Tool messages written by older versions contain the title, status, optional
    // fenced input, and output in one string. Keep that format for saved history,
    // search, exports, and remote clients while presenting the output separately.
    public static (string Summary, string Output) Split(string text)
    {
        var statusStart = text.IndexOf("\n\n*", StringComparison.Ordinal);
        if (statusStart < 0) return (text, "");
        var statusEnd = text.IndexOf('*', statusStart + 3);
        if (statusEnd < 0) return (text, "");
        var end = statusEnd + 1;
        if (text.AsSpan(end).StartsWith("\n\n```", StringComparison.Ordinal))
        {
            var fenceEnd = text.IndexOf("\n```", end + 5, StringComparison.Ordinal);
            if (fenceEnd < 0) return (text, "");
            end = fenceEnd + 4;
        }
        var output = text[end..];
        if (output.StartsWith("\n\n", StringComparison.Ordinal)) output = output[2..];
        else if (output.StartsWith("\r\n\r\n", StringComparison.Ordinal)) output = output[4..];
        return output.Length == 0 ? (text, "") : (text[..end], output);
    }
}
