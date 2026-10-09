namespace CodexManager;

// Windows of a chat's history are measured in conversation messages. Actions (tool calls,
// thinking, plans) collapse into one row between them, so they ride along without counting;
// MaxMessages bounds a window that is nothing but actions.
public static class HistoryWindow
{
    public const int TurnLimit = 12;
    public const int PageSize = 20;
    public const int MaxMessages = 1000;
    public static readonly string[] ActionRoles = ["tool", "thought", "plan"];

    public static bool IsAction(Message message) => ActionRoles.Contains(message.Role);
    public static int Rows(IEnumerable<Message> messages) => messages.Count(m => !IsAction(m));

    // The newest `rows` conversation messages and the actions after the first of them.
    public static Message[] Last(IReadOnlyList<Message> ordered, int rows)
    {
        var count = 0;
        for (var i = ordered.Count - 1; i >= 0; i--)
            if ((!IsAction(ordered[i]) && ++count == rows) || ordered.Count - i == MaxMessages) return ordered.Skip(i).ToArray();
        return ordered.ToArray();
    }

    // The oldest `rows` conversation messages, with the actions that follow the last of them.
    public static Message[] First(IReadOnlyList<Message> ordered, int rows)
    {
        var count = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i + 1 == MaxMessages) return ordered.Take(i + 1).ToArray();
            if (IsAction(ordered[i]) || ++count < rows) continue;
            var end = i + 1;
            while (end < ordered.Count && IsAction(ordered[end]) && end < MaxMessages) end++;
            return ordered.Take(end).ToArray();
        }
        return ordered.ToArray();
    }

    // Keep the current side of the transcript when a page is extended in
    // either direction. The turn cap also bounds tool-heavy turns.
    public static Message[] Bound(IEnumerable<Message> messages, bool newer)
    {
        var ordered = messages.GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).ToArray();
        ordered = newer ? Last(ordered, Chat.HistoryPageSize) : First(ordered, Chat.HistoryPageSize);
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
        var ordered = visible.Concat(page).GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).ToArray();
        return newer ? Last(ordered, Chat.HistoryPageSize) : First(ordered, Chat.HistoryPageSize);
    }
}
