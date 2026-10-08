using System.Text.Json.Nodes;

namespace CodexManager;

// One small row per message of a whole chat: enough to estimate how tall the full transcript is
// while only a window of it is loaded, so the scrollbar spans the chat and scrolling into
// unloaded history does not jump.
public readonly record struct OutlineEntry(int Sequence, string Role, int Chars, int Lines)
{
    public bool IsAction => Role is "tool" or "thought" or "plan";
}

public sealed class TranscriptOutline(string chatKey, OutlineEntry[] entries)
{
    public string ChatKey { get; } = chatKey;
    public OutlineEntry[] Entries { get; } = entries;

    private (int StartSequence, int EndSequence, OutlineEntry First, int Count)[]? rows;
    // Rows as the transcript draws them: consecutive actions collapse into one group row.
    public (int StartSequence, int EndSequence, OutlineEntry First, int Count)[] Rows() => rows ??= BuildRows();
    private (int StartSequence, int EndSequence, OutlineEntry First, int Count)[] BuildRows()
    {
        var rows = new List<(int, int, OutlineEntry, int)>();
        for (var i = 0; i < Entries.Length;)
        {
            var start = i++;
            if (Entries[start].IsAction) while (i < Entries.Length && Entries[i].IsAction) i++;
            rows.Add((Entries[start].Sequence, Entries[i - 1].Sequence, Entries[start], i - start));
        }
        return rows.ToArray();
    }

    public JsonNode ToJson() => new JsonArray(Entries.Select(e => (JsonNode)new JsonArray(e.Sequence, e.Role, e.Chars, e.Lines)).ToArray());
    public static TranscriptOutline? FromJson(string chatKey, JsonNode? node) => node is JsonArray rows
        ? new(chatKey, rows.OfType<JsonArray>().Select(r => new OutlineEntry(r[0]!.GetValue<int>(), r[1]!.GetValue<string>(), r[2]!.GetValue<int>(), r[3]!.GetValue<int>())).ToArray())
        : null;

    // A first guess at a row's height before it is measured; the panel scales it by how far
    // earlier guesses were off.
    public static double Estimate(string role, int chars, int lines, int count, double width)
    {
        if (count > 1) return 36;
        if (role is "tool" or "thought" or "plan") return 44;
        var perLine = Math.Max(20, (width - 64) / 7.2);
        // Whole pixels, so positions summed over many rows stay exact.
        return Math.Round(52 + 19 * (lines + 1 + chars / perLine));
    }
}
