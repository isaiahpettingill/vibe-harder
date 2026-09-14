using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class WebSocketWire
{
    public static async Task<JsonObject> Read(WebSocket socket, CancellationToken token, int maximum = RemoteWire.MaximumFrame)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Expected a JSON text message.");
            if (output.Length + result.Count > maximum) throw new IOException("Remote message is too large.");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(output.GetBuffer().AsSpan(0, (int)output.Length))?.AsObject() ?? throw new IOException("Invalid remote message.");
        }
    }
    public static async Task Write(WebSocket socket, JsonNode value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        if (bytes.Length > RemoteWire.MaximumFrame) throw new IOException("Remote message is too large.");
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
    }
}
