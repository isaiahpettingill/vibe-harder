using System.Text.Json;

namespace CodexManager;

// AIR session records: typed failures and the agent's long-running goal. Both are transcript
// entries that later revisions update in place.
public sealed partial class ChatRuntime
{
    private readonly Dictionary<string, int> failureRevisions = [];
    // The error that ended the current turn, if the agent reported one.
    private (string Id, JsonElement Failure)? turnFailure;
    // Waits before resending a prompt whose turn failed with a passing error. Claude's sign-in
    // refresh fails in every chat but one when they refresh together; spreading them lets it pass.
    public static TimeSpan[] TransientRetryDelays { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    // The agent offers to try again and nothing else is needed: not a usage limit or a sign-in.
    internal static bool Transient(JsonElement failure)
    {
        string[] Values(string name) => failure.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(a => a.GetString() ?? "").ToArray() : [];
        var category = failure.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var actions = Values("actions");
        return actions.Contains("retry") && !actions.Contains("login") && !new[] { "limit", "quota", "billing" }.Any(word => category.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    private void ShowRetrying(string id, int attempt, TimeSpan delay)
    {
        if (chat.Messages.LastOrDefault(m => m.ToolId == "failure:" + id) is not { } message) return;
        var text = message.Text; var offer = text.LastIndexOf("\n\nYou can ", StringComparison.Ordinal);
        if (offer >= 0) text = text[..offer];
        else if (text.LastIndexOf("\n\nTrying again", StringComparison.Ordinal) is var previous and >= 0) text = text[..previous];
        message.Text = text + $"\n\nTrying again in {Math.Round(delay.TotalSeconds)} s (attempt {attempt} of {TransientRetryDelays.Length}).";
        store.SaveMessage(chat, message);
    }

    private void ApplySessionInfo(JsonElement update)
    {
        // Agents that name the session (Claude, Codex, and others) replace the first-line title,
        // unless the user renamed the chat.
        if (update.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString()?.Trim() is { Length: > 0 } named
            && named != chat.Title && !ChatTitles.Renamed(store, chat))
        { chat.Title = named.Length > 250 ? named[..249] + "…" : named; store.Save(chat); }
        if (Air.Of(update) is not { } air) return;
        if (air.TryGetProperty("sessionFailure", out var failure)) ShowFailure(failure);
        if (air.TryGetProperty("goal", out var goal)) ShowGoal(goal);
    }
    // The same incident id with a higher revision updates its entry without moving it.
    private void ShowFailure(JsonElement failure)
    {
        if (failure.ValueKind != JsonValueKind.Object || !failure.TryGetProperty("id", out var idValue) || idValue.GetString() is not { Length: > 0 } id) return;
        var revision = failure.TryGetProperty("revision", out var r) && r.TryGetInt32(out var value) ? value : 1;
        // A retry can fail with the very same report; it still ends this turn.
        if (!replaying && turn is not null && !(failure.TryGetProperty("severity", out var level) && level.GetString() == "warning")) turnFailure = (id, failure.Clone());
        if (failureRevisions.TryGetValue(id, out var seen) && revision <= seen) return;
        failureRevisions[id] = revision;
        string? Text(string name) => failure.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        var warning = Text("severity") == "warning";
        var text = (warning ? "⚠ " : "✖ ") + (Text("title") ?? "The agent reported a problem.");
        if (Text("details") is { Length: > 0 } details) text += "\n\n" + details;
        var actions = failure.TryGetProperty("actions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => a.GetString() switch { "retry" => "try again", "login" => "sign in", "new_session" => "start a new chat", _ => null }).OfType<string>().Distinct().ToArray() : [];
        if (actions.Length > 0) text += "\n\nYou can " + string.Join(" or ", actions) + ".";
        var message = chat.Messages.LastOrDefault(m => m.ToolId == "failure:" + id);
        if (message is null) { Add("system", "", "failure:" + id); message = chat.Messages[^1]; }
        message.Text = text;
        if (!replaying) store.SaveMessage(chat, message);
        if (!warning) chat.Status = Text("title") ?? chat.Status;
    }
    // A goal belongs to the session: it can stay active between prompts and drive autonomous cycles.
    private void ShowGoal(JsonElement goal)
    {
        var message = chat.Messages.LastOrDefault(m => m.ToolId == "goal");
        string text;
        if (goal.ValueKind != JsonValueKind.Object)
        {
            if (message is null) return;
            text = "Goal cleared.";
        }
        else
        {
            string? Text(string name) => goal.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
            var iterations = goal.TryGetProperty("iterations", out var count) && count.TryGetInt32(out var n) && n > 0 ? $" · {n} iteration{(n == 1 ? "" : "s")}" : "";
            text = $"Goal: {Text("objective")}\n\n{Text("status") ?? "active"}{iterations}" + (Text("lastReason") is { Length: > 0 } reason ? "\n\n" + reason : "");
        }
        if (message is null) { Add("system", "", "goal"); message = chat.Messages[^1]; }
        message.Text = text;
        if (!replaying) store.SaveMessage(chat, message);
    }
}
