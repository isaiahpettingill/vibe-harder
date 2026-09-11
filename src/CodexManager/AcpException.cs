using System.Text.Json;

namespace CodexManager;

public sealed class AcpException(JsonElement error) : IOException(Describe(error))
{
    public int Code { get; } = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : 0;
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
