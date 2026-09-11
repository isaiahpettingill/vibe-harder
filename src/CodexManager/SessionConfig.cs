using System.Text.Json;

namespace CodexManager;

public sealed record SessionValue(string Value, string Name)
{
    public override string ToString() => Name;
}
public sealed record SessionConfig(string Id, string Name, string Kind, string Current, IReadOnlyList<SessionValue> Values)
{
    public static IReadOnlyList<SessionConfig> Read(JsonElement response)
    {
        var result = new List<SessionConfig>();
        if (response.TryGetProperty("configOptions", out var configs) && configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var config in configs.EnumerateArray())
            {
                var kind = config.GetProperty("type").GetString();
                if (kind is not "select" and not "boolean") continue;
                var values = kind == "boolean" ? new[] { new SessionValue("false", "Off"), new SessionValue("true", "On") } : ReadValues(config.GetProperty("options")).ToArray();
                var currentValue = config.GetProperty("currentValue");
                var current = kind == "boolean" ? (currentValue.GetBoolean() ? "true" : "false") : currentValue.GetString()!;
                result.Add(new(config.GetProperty("id").GetString()!, config.GetProperty("name").GetString()!, kind, current, values));
            }
            return result;
        }
        foreach (var (key, kind, current, options, value) in new[] { ("models", "model", "currentModelId", "availableModels", "modelId"), ("modes", "mode", "currentModeId", "availableModes", "id") })
            if (response.TryGetProperty(key, out var group) && group.TryGetProperty(options, out var choices))
                result.Add(new(kind, kind == "model" ? "Model" : "Mode", kind, group.GetProperty(current).GetString()!, choices.EnumerateArray().Select(c => new SessionValue(c.GetProperty(value).GetString()!, c.GetProperty("name").GetString()!)).ToArray()));
        return result;
    }
    private static IEnumerable<SessionValue> ReadValues(JsonElement options)
    {
        foreach (var value in options.EnumerateArray())
        {
            if (value.TryGetProperty("options", out var children)) { foreach (var child in ReadValues(children)) yield return child; }
            else yield return new(value.GetProperty("value").GetString()!, value.GetProperty("name").GetString()!);
        }
    }
}

