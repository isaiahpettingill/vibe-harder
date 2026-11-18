using System.Text;
using System.Text.RegularExpressions;

namespace CodexManager;

public sealed partial class TerminalOutput
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private string raw = "";
    public string Text { get; private set; } = "";
    public string? Link { get; private set; }
    public void Append(byte[] bytes)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        Append(new string(chars, 0, count));
    }
    internal void Append(string text)
    {
        if (text.Length == 0) return;
        raw += text;
        if (raw.Length > 65536) raw = raw[^65536..];
        Text = EscapeCodes().Replace(raw, "");
        // Wait for a delimiter, so a URL split between PTY reads is never opened halfway.
        var matches = WebLinks().Matches(Text);
        if (matches.Count > 0) Link = matches[^1].Value;
    }
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))")]
    private static partial Regex EscapeCodes();
    [GeneratedRegex("https?://[^\\s<>\"\\x1B]+(?=[\\s<>\"\\x1B])")]
    private static partial Regex WebLinks();
}
