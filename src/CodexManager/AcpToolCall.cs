using System.Text;
using System.Text.Json;

namespace CodexManager;

// One tool call as reported by the agent. ACP updates carry only the fields that changed
// (AIR also drops unchanged _meta keys), so each report merges into this state, and the
// transcript text is rendered from the whole state.
public sealed class AcpToolCall
{
    private const int MaxOutput = 512 * 1024;
    public string Title { get; private set; } = "";
    public string Status { get; private set; } = "pending";
    public string? CommandTitle { get; private set; }
    public string Input { get; private set; } = "";
    public string Content { get; private set; } = "";
    public string RawOutput { get; private set; } = "";
    public bool Backgrounded { get; private set; }
    public int? ExitCode { get; private set; }
    private readonly StringBuilder output = new();
    private bool outputTrimmed;

    public static AcpToolCall FromText(string text)
    {
        // Rebuilds the state of a tool call saved before this run, so later updates merge into it.
        var sections = ToolMessageContent.Locate(text);
        var call = new AcpToolCall();
        var summary = text[..sections.SummaryEnd];
        var statusStart = summary.IndexOf("\n\n*", StringComparison.Ordinal);
        call.Title = statusStart < 0 ? summary : summary[..statusStart];
        if (statusStart >= 0 && summary.IndexOf('*', statusStart + 3) is > 0 and var statusEnd) call.Status = summary[(statusStart + 3)..statusEnd];
        if (sections.HasCommand) call.Input = "\n\n" + text[sections.CommandStart..sections.CommandEnd];
        if (sections.HasOutput(text.Length)) call.Content = text[sections.OutputStart..];
        return call;
    }

    public void Merge(JsonElement update)
    {
        if (update.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) Title = title.GetString() ?? Title;
        if (update.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String) Status = status.GetString() ?? Status;
        if (update.TryGetProperty("rawInput", out var input) && input.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
            Input = "\n\n```\n" + InputText(input) + "\n```";
        if (update.TryGetProperty("content", out var contents) && contents.ValueKind == JsonValueKind.Array) Content = ContentText(contents);
        if (update.TryGetProperty("rawOutput", out var rawOutput)) RawOutput = OutputText(rawOutput);
        if (update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object) MergeMeta(meta);
    }
    private void MergeMeta(JsonElement meta)
    {
        foreach (var channel in new[] { "terminal_output_delta", "terminal_output" })
            if (meta.TryGetProperty(channel, out var chunk) && chunk.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String) Append(data.GetString()!);
        if (meta.TryGetProperty("terminal_exit", out var exit) && exit.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number) ExitCode = code.GetInt32();
        if (Air.Meta(meta) is { } air)
        {
            if (air.TryGetProperty("commandTitle", out var commandTitle) && commandTitle.ValueKind == JsonValueKind.String) CommandTitle = commandTitle.GetString();
            if (air.TryGetProperty("asyncTasks", out var tasks) && tasks.TryGetProperty("backgrounded", out var backgrounded)) Backgrounded = backgrounded.ValueKind == JsonValueKind.True;
        }
    }
    private void Append(string data)
    {
        output.Append(data);
        // Keep the latest output of a long-running command, not all of it.
        if (output.Length > MaxOutput) { output.Remove(0, output.Length - MaxOutput / 2); outputTrimmed = true; }
    }

    public string Render()
    {
        var status = Backgrounded && Status is "completed" or "in_progress" or "pending" ? "running in background" : Status;
        var heading = string.IsNullOrWhiteSpace(CommandTitle) ? Title : CommandTitle;
        var details = new StringBuilder();
        if (Content.Length > 0) details.Append(Content.TrimStart('\n'));
        if (output.Length > 0)
        {
            if (details.Length > 0) details.Append("\n\n");
            details.Append(Fence((outputTrimmed ? "…\n" : "") + output.ToString().TrimEnd('\n'), "console"));
        }
        if (details.Length == 0 && RawOutput.Length > 0) details.Append(RawOutput);
        if (ExitCode is { } code and not 0 && Status != "failed") details.Append($"\n\n*exit code {code}*");
        var input = heading == Title || Input.Length > 0 ? Input : "\n\n```\n" + Title + "\n```";
        return $"{OneLine(heading)}\n\n*{status}*{input}{(details.Length > 0 ? "\n\n" : "")}{details}";
    }
    // The heading is the first line of the saved text, and the transcript splits on blank lines.
    private static string OneLine(string text) => string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string InputText(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String) return input.GetString() ?? "";
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var command))
        {
            if (command.ValueKind == JsonValueKind.String) return command.GetString() ?? "";
            if (command.ValueKind == JsonValueKind.Array) return string.Join(' ', command.EnumerateArray().Select(part => part.ToString()));
        }
        return JsonSerializer.Serialize(input, AirJsonContext.Default.JsonElement);
    }
    private static string ContentText(JsonElement contents)
    {
        var text = new StringBuilder();
        foreach (var item in contents.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? part = type switch
            {
                "content" when item.TryGetProperty("content", out var inner) && inner.TryGetProperty("text", out var value) => value.GetString(),
                "diff" => Diff(item),
                _ => null
            };
            if (string.IsNullOrEmpty(part)) continue;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(part);
        }
        return text.ToString();
    }
    private static string OutputText(JsonElement rawOutput) => rawOutput.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => rawOutput.GetString() ?? "",
        // MCP results: show their text and error message rather than the JSON envelope.
        JsonValueKind.Object when rawOutput.TryGetProperty("result", out var result) || rawOutput.TryGetProperty("error", out _) => McpText(rawOutput),
        _ => rawOutput.GetRawText()
    };
    private static string McpText(JsonElement output)
    {
        var text = new StringBuilder();
        if (output.TryGetProperty("result", out var result) && result.TryGetProperty("content", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) text.Append(value.GetString()).Append('\n');
        if (output.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) text.Append("Error: ").Append(message.GetString());
        return text.Length > 0 ? text.ToString().TrimEnd() : output.GetRawText();
    }

    // AIR sends one Git patch per file; standard ACP sends the old and new text.
    private static string Diff(JsonElement item)
    {
        var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (item.TryGetProperty("_meta", out var meta) && Air.Meta(meta) is { } air && air.TryGetProperty("diffPatch", out var patch)
            && patch.TryGetProperty("format", out var format) && format.GetString() == "git_patch" && patch.TryGetProperty("text", out var patchText) && patchText.ValueKind == JsonValueKind.String)
            return Fence(patchText.GetString()!.TrimEnd('\n'), "diff");
        var oldText = item.TryGetProperty("oldText", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : null;
        var newText = item.TryGetProperty("newText", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        return Fence(LineDiff.Unified(path, oldText, newText), "diff");
    }
    private static string Fence(string text, string language)
    {
        var fence = "```";
        while (text.Contains(fence, StringComparison.Ordinal)) fence += "`";
        return fence + language + "\n" + text + "\n" + fence;
    }
}

// Line diff for standard ACP diff blocks, which carry whole texts or hunks without a patch.
public static class LineDiff
{
    public static string Unified(string path, string? oldText, string newText)
    {
        var header = $"--- {(oldText is null ? "/dev/null" : "a/" + path.TrimStart('/'))}\n+++ b/{path.TrimStart('/')}\n";
        var before = oldText is null || oldText.Length == 0 ? [] : oldText.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var after = newText.Length == 0 ? [] : newText.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var lines = new StringBuilder(header);
        // A quadratic table is fine for snippets; very large texts show as a replacement.
        if ((long)before.Length * after.Length > 4_000_000)
        {
            foreach (var line in before) lines.Append('-').Append(line).Append('\n');
            foreach (var line in after) lines.Append('+').Append(line).Append('\n');
            return lines.ToString().TrimEnd('\n');
        }
        var common = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
            for (var j = after.Length - 1; j >= 0; j--)
                common[i, j] = before[i] == after[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        int x = 0, y = 0;
        while (x < before.Length || y < after.Length)
        {
            if (x < before.Length && y < after.Length && before[x] == after[y]) { lines.Append(' ').Append(before[x]).Append('\n'); x++; y++; }
            // Removals before additions, as unified diffs show a replacement.
            else if (x < before.Length && (y == after.Length || common[x + 1, y] >= common[x, y + 1])) lines.Append('-').Append(before[x++]).Append('\n');
            else lines.Append('+').Append(after[y++]).Append('\n');
        }
        return lines.ToString().TrimEnd('\n');
    }
}
