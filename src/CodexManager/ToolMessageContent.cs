namespace CodexManager;

internal static class ToolMessageContent
{
    public readonly record struct Sections(int SummaryEnd, int CommandStart, int CommandEnd, int OutputStart)
    {
        public bool HasCommand => CommandEnd > CommandStart;
        public bool HasOutput(int length) => OutputStart < length;
    }

    public static Sections Locate(string text)
    {
        var statusStart = text.IndexOf("\n\n*", StringComparison.Ordinal);
        if (statusStart < 0) return new(text.Length, text.Length, text.Length, text.Length);
        var statusEnd = text.IndexOf('*', statusStart + 3);
        if (statusEnd < 0) return new(text.Length, text.Length, text.Length, text.Length);
        var end = statusEnd + 1;
        var commandStart = end;
        if (text.AsSpan(end).StartsWith("\n\n```", StringComparison.Ordinal))
        {
            var fenceEnd = text.IndexOf("\n```", end + 5, StringComparison.Ordinal);
            if (fenceEnd < 0) return new(text.Length, text.Length, text.Length, text.Length);
            commandStart = end + 2;
            end = fenceEnd + 4;
        }
        var outputStart = end;
        if (text.AsSpan(end).StartsWith("\n\n", StringComparison.Ordinal)) outputStart += 2;
        else if (text.AsSpan(end).StartsWith("\r\n\r\n", StringComparison.Ordinal)) outputStart += 4;
        return new(end, commandStart, end, outputStart);
    }

    // Tool messages written by older versions contain the title, status, optional
    // fenced input, and output in one string. Keep that format for saved history,
    // search, exports, and remote clients while presenting the output separately.
    public static (string Summary, string Output) Split(string text)
    {
        var sections = Locate(text);
        return sections.HasOutput(text.Length) ? (text[..sections.SummaryEnd], text[sections.OutputStart..]) : (text, "");
    }
}
