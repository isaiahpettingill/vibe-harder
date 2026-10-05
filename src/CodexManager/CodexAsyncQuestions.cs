using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

// Codex asks questions and sends updates during a turn through request_user_input_async and
// send_message_to_user_async. Both complete as whole agent messages without streamed text, which
// codex-acp drops, so they never reach the client. Codex still records the tool calls in the
// session rollout; follow that file to show them and answer in Codex's reply envelope.
public sealed class CodexAsyncQuestions(Func<Task<string>> codexHome, string sessionId, DateTimeOffset started)
{
    public sealed record Question(string Title, string[] Options);
    public sealed record Item(string CallId, string? Message, Question[] Questions);

    private string? path;
    private long offset = -1;
    private DateTimeOffset nextSearch;
    private bool searchedAll;
    private string? home;
    private readonly StringBuilder partial = new();

    // Reads items recorded since the first call; earlier ones are already in the replayed history.
    public async Task<Item[]> Read(CancellationToken token)
    {
        if (path is null)
        {
            if (DateTimeOffset.UtcNow < nextSearch) return [];
            nextSearch = DateTimeOffset.UtcNow.AddSeconds(15);
            // Search every folder once; resumed sessions keep their original date.
            var deep = !searchedAll; searchedAll = true;
            home ??= await codexHome();
            path = await Task.Run(() => Find(home, sessionId, started, deep), token);
            if (path is null) { offset = 0; return []; }
        }
        return await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset < 0 || offset > stream.Length) { offset = stream.Length; partial.Clear(); return []; }
            stream.Position = offset;
            var bytes = new byte[stream.Length - offset];
            stream.ReadExactly(bytes);
            offset += bytes.Length;
            partial.Append(Encoding.UTF8.GetString(bytes));
            var text = partial.ToString();
            var end = text.LastIndexOf('\n');
            partial.Clear();
            if (end < 0) { partial.Append(text); return []; }
            partial.Append(text[(end + 1)..]);
            return text[..end].Split('\n').Select(Parse).OfType<Item>().ToArray();
        }, token);
    }

    // Rollouts live in sessions/YYYY/MM/DD under the host's local creation date.
    private static string? Find(string home, string sessionId, DateTimeOffset started, bool deep)
    {
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions)) return null;
        var pattern = "rollout-*" + sessionId + ".jsonl";
        foreach (var day in new[] { started.UtcDateTime, DateTime.UtcNow }.SelectMany(d => new[] { d.AddDays(-1), d, d.AddDays(1) }).Select(d => d.Date).Distinct())
        {
            var folder = Path.Combine(sessions, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, pattern).FirstOrDefault() is { } found) return found;
        }
        return deep ? Directory.EnumerateFiles(sessions, pattern, SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    public static Item? Parse(string line)
    {
        if (!line.Contains("_async", StringComparison.Ordinal)) return null;
        try
        {
            var entry = JsonNode.Parse(line);
            if (entry?["type"]?.GetValue<string>() != "response_item" || entry["payload"] is not JsonObject payload) return null;
            if (payload["type"]?.GetValue<string>() != "function_call" || payload["call_id"]?.GetValue<string>() is not { } callId) return null;
            var name = payload["name"]?.GetValue<string>();
            if (name is not ("request_user_input_async" or "send_message_to_user_async")) return null;
            var arguments = JsonNode.Parse(payload["arguments"]?.GetValue<string>() ?? "{}");
            if (name == "send_message_to_user_async")
                return arguments?["message"]?.GetValue<string>() is { Length: > 0 } message ? new Item(callId, message, []) : null;
            var questions = (arguments?["questions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(q => new Question(q["title"]?.GetValue<string>() ?? "", (q["options"] as JsonArray ?? []).Select(o => o?.GetValue<string>() ?? "").Where(o => o.Length > 0).ToArray()))
                .Where(q => q.Title.Trim().Length > 0).ToArray();
            return questions.Length > 0 ? new Item(callId, null, questions) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // Matches how Codex renders the item in history, so replay and the live view read the same.
    public static string Text(Item item) => item.Message ?? string.Join("\n\n", item.Questions.Select(q =>
        "**" + q.Title.Trim() + "**" + string.Concat(q.Options.Select(o => "\n- " + o))));

    public static JsonObject Form(Item item)
    {
        var properties = new JsonObject();
        for (var i = 0; i < item.Questions.Length; i++)
        {
            var question = item.Questions[i];
            var field = new JsonObject { ["type"] = "string", ["title"] = question.Title };
            if (question.Options.Length > 0)
            {
                field["oneOf"] = new JsonArray(question.Options.Select(o => (JsonNode)new JsonObject { ["const"] = o, ["title"] = o }).ToArray());
                field["default"] = question.Options[0];
                properties[$"q{i}"] = field;
                properties[$"q{i}_other"] = new JsonObject { ["type"] = "string", ["title"] = "Or answer in your own words" };
            }
            else properties[$"q{i}"] = field;
        }
        return new JsonObject
        {
            ["mode"] = "form",
            ["message"] = "Codex is still working. Your answer is sent to it as your next message.",
            ["requestedSchema"] = new JsonObject { ["type"] = "object", ["title"] = "Question from Codex", ["properties"] = properties, ["required"] = new JsonArray() }
        };
    }

    // Codex recognizes this envelope as answers to its async questions.
    public static string? Reply(Item item, JsonObject content)
    {
        var replies = new JsonArray();
        for (var i = 0; i < item.Questions.Length; i++)
        {
            var other = content[$"q{i}_other"]?.GetValue<string>()?.Trim();
            var answer = !string.IsNullOrEmpty(other) ? other : content[$"q{i}"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(answer)) continue;
            var title = item.Questions[i].Title;
            replies.Add((JsonNode)new JsonObject
            {
                ["answer"] = answer,
                ["question"] = Truncate(title).Replace('\n', ' ').Replace('\r', ' '),
                ["questionItemId"] = new JsonArray("request_user_input_async", item.CallId, i).ToJsonString(Plain)
            });
        }
        return replies.Count == 0 ? null : "<send_user_message_question_reply>\n" + replies.ToJsonString(Plain) + "\n</send_user_message_question_reply>";
    }
    // Match Codex's own serializer instead of escaping quotes and non-ASCII text.
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Truncate(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= 512) return text;
        var end = 0;
        while (end < text.Length && Encoding.UTF8.GetByteCount(text.AsSpan(0, end + 1)) <= 512) end++;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }

    // Shows a reply envelope the way Codex's own clients do: each question quoted above its answer.
    public static string Display(string text)
    {
        const string open = "<send_user_message_question_reply>", close = "</send_user_message_question_reply>";
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(open, StringComparison.Ordinal) || !trimmed.EndsWith(close, StringComparison.Ordinal)) return text;
        try
        {
            var body = JsonNode.Parse(trimmed[open.Length..^close.Length]);
            var replies = body is JsonArray array ? array.ToArray() : [body];
            if (replies.Length == 0 || replies.Any(r => r?["question"] is null || r["answer"] is null)) return text;
            return string.Join("\n\n", replies.Select(r => "> " + r!["question"]!.GetValue<string>() + "\n\n" + r["answer"]!.GetValue<string>()));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return text; }
    }
}
