using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

public static class ElicitationForm
{
    public static JsonObject Cancel() => new() { ["action"] = "cancel" };
    public static JsonObject Decline() => new() { ["action"] = "decline" };
    public static JsonObject Accept(JsonObject content) => new() { ["action"] = "accept", ["content"] = content.DeepClone() };

    public static JsonObject Schema(JsonObject request)
    {
        if (request["mode"]?.GetValue<string>() != "form") throw new ArgumentException("Unsupported elicitation mode.");
        if (request["requestedSchema"] is not JsonObject schema) throw new ArgumentException("The form schema is missing.");
        if (schema["properties"] is not JsonObject) throw new ArgumentException("The form properties are missing.");
        return schema;
    }

    public static string? Validate(JsonObject request, JsonObject content)
    {
        var schema = Schema(request);
        var properties = schema["properties"]!.AsObject();
        var required = schema["required"] as JsonArray;
        foreach (var name in content.Select(field => field.Key))
            if (!properties.ContainsKey(name)) return $"Unknown field: {name}.";
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject field) return $"Unsupported field: {name}.";
            var value = content[name];
            var mustProvide = required?.Any(item => item?.GetValue<string>() == name) == true;
            if (value is null)
            {
                if (mustProvide) return $"{Label(name, field)} is required.";
                continue;
            }
            var type = field["type"]?.GetValue<string>();
            if (type == "boolean")
            {
                if (value is not JsonValue v || !v.TryGetValue<bool>(out _)) return $"{Label(name, field)} must be yes or no.";
            }
            else if (type is "number" or "integer")
            {
                if (value is not JsonValue v || !double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                    type == "integer" && number != Math.Truncate(number)) return $"{Label(name, field)} must be a valid {type}.";
                if (field["minimum"] is JsonValue min && number < min.GetValue<double>()) return $"{Label(name, field)} is too small.";
                if (field["maximum"] is JsonValue max && number > max.GetValue<double>()) return $"{Label(name, field)} is too large.";
            }
            else if (type == "array")
            {
                if (value is not JsonArray selected) return $"{Label(name, field)} must be a list.";
                var options = Choices(field);
                if (options.Count == 0 || selected.Any(item => item is null || !options.Any(option => JsonNode.DeepEquals(option.Value, item)))) return $"Select valid options for {Label(name, field)}.";
                if (field["minItems"] is JsonValue min && selected.Count < min.GetValue<int>()) return $"Select more options for {Label(name, field)}.";
                if (field["maxItems"] is JsonValue max && selected.Count > max.GetValue<int>()) return $"Select fewer options for {Label(name, field)}.";
            }
            else if (type == "string")
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var input)) return $"{Label(name, field)} must be text.";
                if (Choices(field) is { Count: > 0 } choices && !choices.Any(choice => JsonNode.DeepEquals(choice.Value, value))) return $"Select an option for {Label(name, field)}.";
                if (field["minLength"] is JsonValue min && input.Length < min.GetValue<int>()) return $"{Label(name, field)} is too short.";
                if (field["maxLength"] is JsonValue max && input.Length > max.GetValue<int>()) return $"{Label(name, field)} is too long.";
                if (field["pattern"] is JsonValue pattern)
                {
                    try { if (!Regex.IsMatch(input, pattern.GetValue<string>(), RegexOptions.None, TimeSpan.FromMilliseconds(100))) return $"{Label(name, field)} has an invalid format."; }
                    catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException) { return $"{Label(name, field)} has an unsupported format rule."; }
                }
            }
            else return $"Unsupported field type for {Label(name, field)}.";
        }
        return null;
    }

    public static string Label(string name, JsonObject field) => field["title"]?.GetValue<string>() ?? name;

    public static IReadOnlyList<(JsonNode Value, string Label, string? Description)> Choices(JsonObject field)
    {
        var source = field["type"]?.GetValue<string>() == "array" ? field["items"] as JsonObject : field;
        if (source is null) return [];
        if (source["oneOf"] is JsonArray oneOf) return oneOf.OfType<JsonObject>().Where(o => o["const"] is not null)
            .Select(o => (o["const"]!.DeepClone(), o["title"]?.GetValue<string>() ?? o["const"]!.ToJsonString(), o["description"]?.GetValue<string>())).ToArray();
        if (source["anyOf"] is JsonArray anyOf) return anyOf.OfType<JsonObject>().Where(o => o["const"] is not null)
            .Select(o => (o["const"]!.DeepClone(), o["title"]?.GetValue<string>() ?? o["const"]!.ToJsonString(), o["description"]?.GetValue<string>())).ToArray();
        if (source["enum"] is JsonArray values) return values.Where(v => v is not null)
            .Select(v => (v!.DeepClone(), v.GetValue<string>(), (string?)null)).ToArray();
        return [];
    }
}
