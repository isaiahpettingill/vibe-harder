using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class PermissionPolicy
{
    public static string? AllowedOption(JsonElement request)
    {
        if (!request.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return null;
        foreach (var kind in new[] { "allow_once", "allow_always" })
            foreach (var option in options.EnumerateArray())
                if (option.TryGetProperty("kind", out var value) && value.GetString() == kind && option.TryGetProperty("optionId", out var id)) return id.GetString();
        return null;
    }
    public static JsonObject? AutoApprove(Store store, JsonElement request) => store.Setting("allowAllPermissions") == "1" && AllowedOption(request) is { } id ? RpcJson.Permission(id) : null;
}
