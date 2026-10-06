using System.Text.Json.Nodes;

namespace CodexManager;

// Push notifications from paired computers through UnifiedPush, installed by the Android app.
// Any installed distributor works; push is optional and pairing works without it.
public interface IMobilePush
{
    bool Enabled { get; }
    // What the user should know: the distributor in use, or why push is unavailable.
    string Status { get; }
    event Action? Changed;
    // Turning push on asks for notification permission; a refusal leaves it off.
    Task SetEnabled(bool enabled);
    // Registers each paired computer that lacks a registration and drops removed ones.
    void HostsChanged();
    // Lets the user pick among several installed distributors.
    void ChooseDistributor();
    bool CanChooseDistributor { get; }
}

public static class MobilePush
{
    public static IMobilePush? Current { get; set; }

    // The computer's VAPID key, which the distributor needs for this registration.
    public static async Task<string> VapidKey(RemoteHost host, CancellationToken token)
    {
        using var connection = await Connect(host, token);
        return (await connection.Request(new() { ["method"] = "push/vapid" }, token))!["publicKey"]!.GetValue<string>();
    }
    // Sends the endpoint to the computer over the paired, authenticated connection.
    public static async Task Register(RemoteHost host, string endpoint, string publicKey, string auth, string? distributor, CancellationToken token)
    {
        using var connection = await Connect(host, token);
        await connection.Request(new() { ["method"] = "push/register", ["endpoint"] = endpoint, ["p256dh"] = publicKey, ["auth"] = auth, ["distributor"] = distributor }, token);
    }
    public static async Task Unregister(RemoteHost host, CancellationToken token)
    {
        using var connection = await Connect(host, token);
        await connection.Request(new() { ["method"] = "push/unregister" }, token);
    }
    private static async Task<RemoteConnection> Connect(RemoteHost host, CancellationToken token)
    {
        var connection = new RemoteConnection(host);
        try { await connection.Connect(token); return connection; }
        catch { connection.Dispose(); throw; }
    }
    // A host is identified by its address and port, the same key its saved pairing uses.
    public static string Key(RemoteHost host) => host.Address + ":" + host.Port;
    public static JsonObject? ReadPayload(byte[] cleartext)
    {
        try { return JsonNode.Parse(cleartext) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
