using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodexManager;

// JetBrains AIR extensions to ACP, implemented by codex-acp and claude-agent-acp. Declaring
// AIR makes the adapters send compact tool calls, typed failures, background tasks, and
// presentation records; see their docs/air-extensions.md.
public static class Air
{
    // planFile is not declared: the plan file lives on the agent's host, which remote and mobile
    // clients cannot read, so plans stay inline. rawInputRendering is not declared: readable input
    // (questions, plans, prompts) keeps its display copy in the tool call content.
    public static readonly string[] Capabilities = ["asyncTasks", "sessionFailure", "diffPatch", "recommendedValue", "nativeSubagentSessions"];

    public static JsonObject ClientMeta() => new()
    {
        ["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["version"] = 1, ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode)c).ToArray()) } },
        // AIR sends command output only through a declared chunk channel.
        ["terminal_output_delta"] = true
    };

    public static JsonElement? Meta(JsonElement meta) =>
        meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("jetbrains", out var jetbrains) && jetbrains.ValueKind == JsonValueKind.Object
        && jetbrains.TryGetProperty("air", out var air) && air.ValueKind == JsonValueKind.Object ? air : null;
    public static JsonNode? Meta(JsonNode? meta) => meta?["jetbrains"]?["air"];
    public static JsonElement? Of(JsonElement message) => message.ValueKind == JsonValueKind.Object && message.TryGetProperty("_meta", out var meta) ? Meta(meta) : null;
}

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AirJsonContext : JsonSerializerContext;
