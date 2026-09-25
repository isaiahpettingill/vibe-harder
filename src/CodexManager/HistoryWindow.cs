namespace CodexManager;

public static class HistoryWindow
{
    public const int TurnLimit = 12;

    // Keep the current side of the transcript when a page is extended in
    // either direction. The message cap also bounds tool-heavy turns.
    public static Message[] Bound(IEnumerable<Message> messages, bool newer)
    {
        var ordered = messages.GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).ToArray();
        if (ordered.Length > Chat.HistoryPageSize)
            ordered = newer ? ordered[^Chat.HistoryPageSize..] : ordered[..Chat.HistoryPageSize];
        var turns = 0;
        if (newer)
        {
            for (var i = ordered.Length - 1; i >= 0; i--)
                if (ordered[i].Role == "user" && ++turns == TurnLimit) return ordered[i..];
        }
        else
        {
            for (var i = 0; i < ordered.Length; i++)
                if (ordered[i].Role == "user" && ++turns > TurnLimit) return ordered[..i];
        }
        return ordered;
    }

    public static Message[] Navigate(IReadOnlyList<Message> visible, IReadOnlyList<Message> page, bool newer)
    {
        const int overlap = 4;
        return newer
            ? Bound(visible.TakeLast(overlap).Concat(page), newer: false)
            : Bound(page.Concat(visible.Take(overlap)), newer: true);
    }
}
