using System.Text.Json.Nodes;

namespace CodexManager;

// Explicit JSON nodes keep the wire protocol independent of runtime reflection.
public static class RpcJson
{
    public static JsonObject Object(params (string Name, JsonNode? Value)[] members)
    {
        var result = new JsonObject();
        foreach (var (name, value) in members) result[name] = value;
        return result;
    }
    public static JsonObject Permission(string? option = null) => Object(("outcome", option is null
        ? Object(("outcome", "cancelled"))
        : Object(("outcome", "selected"), ("optionId", option))));
}
