using System.Text.Json;

namespace CodexManager;

public sealed class AcpException(JsonElement error) : IOException(Describe(error))
{
    public int Code { get; } = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : 0;
    public bool AuthenticationRequired { get; } = HasAuthenticationError(error);
    private static bool HasAuthenticationError(JsonElement value, int depth = 0)
    {
        if (depth > 5) return false;
        if (value.ValueKind == JsonValueKind.String) return AgentProviders.AuthenticationText(value.GetString() ?? "");
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "status" or "statusCode" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var status) && status == 401) return true;
            if (property.Name is "code" or "type" or "reason" or "message" or "details" or "error" or "data" && HasAuthenticationError(property.Value, depth + 1)) return true;
        }
        return false;
    }
    private static string Describe(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "Agent request failed" : "Agent request failed";
        if (error.TryGetProperty("data", out var data))
        {
            var detail = data.ValueKind == JsonValueKind.String ? data.GetString() :
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (!string.IsNullOrWhiteSpace(detail)) message += ": " + detail;
        }
        return message;
    }
}
