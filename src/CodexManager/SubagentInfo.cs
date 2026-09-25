using System.Text.Json;

namespace CodexManager;

public sealed record SubagentEntry(string Id, string Role, string Text, SubagentInfo? Child = null);
public sealed record SubagentInfo(string Title, string Agent, string Prompt, string Status, string? SessionId, string Output, SubagentEntry[] Activity)
{
    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    public static SubagentInfo? FromTool(JsonElement update, SubagentInfo? previous = null)
    {
        var result = previous;
        if (update.TryGetProperty("rawInput", out var input) && input.ValueKind == JsonValueKind.Object &&
            (Text(input, "subagent_type") ?? Text(input, "agent_type")) is { } agent)
            result = new(Text(input, "description") ?? Text(update, "title") ?? "Subagent", agent, Text(input, "prompt") ?? "", Text(update, "status") ?? previous?.Status ?? "running", previous?.SessionId, previous?.Output ?? "", previous?.Activity ?? []);
        if (result is null) return null;
        var output = Content(update);
        return result with { Status = Text(update, "status") ?? result.Status, Output = update.TryGetProperty("content", out _) ? output : result.Output };
    }
    public static string Content(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.Object) return Text(content, "text") ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n\n", content.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("content", out var nested) ? Text(nested, "text") : Text(c, "text")).OfType<string>());
    }
    public static SubagentInfo? FromLegacy(string text)
    {
        var start = text.IndexOf("```\n", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf("\n```", start + 4, StringComparison.Ordinal);
        if (end < 0 || end - start > 1_000_000) return null;
        var fenced = text.AsSpan(start + 4, end - start - 4);
        if (!fenced.Contains("\"subagent_type\"", StringComparison.Ordinal) && !fenced.Contains("\"agent_type\"", StringComparison.Ordinal)) return null;
        try
        {
            using var input = JsonDocument.Parse(text[(start + 4)..end]);
            var agent = Text(input.RootElement, "subagent_type") ?? Text(input.RootElement, "agent_type");
            if (agent is null) return null;
            var statusStart = text.IndexOf("\n\n*", StringComparison.Ordinal);
            var statusEnd = statusStart < 0 ? -1 : text.IndexOf('*', statusStart + 3);
            return new(Text(input.RootElement, "description") ?? text.Split('\n')[0], agent, Text(input.RootElement, "prompt") ?? "", statusEnd < 0 ? "" : text[(statusStart + 3)..statusEnd], null, text[(end + 4)..].Trim(), []);
        }
        catch (JsonException) { return null; }
    }
    public SubagentInfo Apply(string session, Func<SubagentInfo, SubagentInfo> change)
    {
        if (SessionId == session) return change(this);
        var activity = Activity.Select(entry => entry.Child is { } child ? entry with { Child = child.Apply(session, change) } : entry).ToArray();
        return this with { Activity = activity };
    }
    public int Depth(string session, int depth = 0) => SessionId == session ? depth : Activity.Where(e => e.Child is not null).Select(e => e.Child!.Depth(session, depth + 1)).DefaultIfEmpty(-1).Max();
    public SubagentInfo Disconnected() => this with
    {
        Status = Status is "running" or "in_progress" or "pending" ? "disconnected" : Status,
        Activity = Activity.Select(e => e.Child is null ? e : e with { Child = e.Child.Disconnected() }).ToArray()
    };
    public SubagentInfo Append(JsonElement update)
    {
        var kind = Text(update, "sessionUpdate");
        var entries = Activity.ToList();
        if (kind is "agent_message_chunk" or "agent_thought_chunk" or "user_message_chunk")
        {
            var role = kind == "agent_message_chunk" ? "assistant" : kind == "agent_thought_chunk" ? "thought" : "user";
            if (entries.LastOrDefault() is { Child: null } last && last.Role == role) entries[^1] = last with { Text = last.Text + Content(update) };
            else entries.Add(new(Guid.NewGuid().ToString("N"), role, Content(update)));
        }
        else if (kind is "tool_call" or "tool_call_update")
        {
            var id = Text(update, "toolCallId"); if (id is null) return this;
            var index = entries.FindIndex(e => e.Id == id);
            var previous = index < 0 ? null : entries[index];
            var title = Text(update, "title") ?? previous?.Text.Split('\n')[0] ?? "Tool";
            var oldParts = previous?.Text.Split(["\n\n"], 3, StringSplitOptions.None);
            var detail = update.TryGetProperty("content", out _) ? Content(update) : oldParts is { Length: 3 } ? oldParts[2] : "";
            if (update.TryGetProperty("rawInput", out var input)) detail = "```\n" + input.GetRawText() + "\n```\n\n" + detail;
            var state = Text(update, "status") ?? (oldParts is { Length: >= 2 } ? oldParts[1] : "");
            var entry = new SubagentEntry(id, "tool", title + "\n\n" + state + "\n\n" + detail, FromTool(update, previous?.Child));
            if (index < 0) entries.Add(entry); else entries[index] = entry;
        }
        else if (kind == "plan" && update.TryGetProperty("entries", out var plan))
            entries.Add(new(Guid.NewGuid().ToString("N"), "plan", string.Join("\n", plan.EnumerateArray().Select(e => "- " + Text(e, "content")))));
        else return this;
        return this with { Activity = entries.TakeLast(128).ToArray() };
    }
}
