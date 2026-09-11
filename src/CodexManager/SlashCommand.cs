using System.Text.Json;

namespace CodexManager;

public sealed record SlashCommand(string Name, string Description, string? Hint)
{
    public override string ToString() => "/" + Name + (Hint is null ? "" : "  " + Hint) + " — " + Description;
    public static IReadOnlyList<SlashCommand> Read(JsonElement update) => update.GetProperty("availableCommands").EnumerateArray()
        .Where(c => c.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
        .Select(c => new SlashCommand(c.GetProperty("name").GetString()!, c.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            c.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("hint", out var hint) ? hint.GetString() : null)).ToArray();
    public static IReadOnlyList<SlashCommand> Match(IReadOnlyList<SlashCommand> commands, string text) =>
        text.StartsWith('/') && !text.Any(char.IsWhiteSpace) ? commands.Where(c => c.Name.StartsWith(text[1..], StringComparison.OrdinalIgnoreCase)).ToArray() : [];
}
