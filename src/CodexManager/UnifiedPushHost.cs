using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

// The computer's side of UnifiedPush. A paired phone registers one push endpoint per computer
// over its authenticated connection; the endpoint and keys are mutable metadata of that device,
// so revoking the device drops them too. Notifications are Web Push messages to the endpoint.
public static class UnifiedPushHost
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public const string Subject = "https://github.com/isaiahpettingill/vibe-harder";
    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Send { get; set; } = request => Http.SendAsync(request);

    public static bool Handles(string? method) => method is "push/vapid" or "push/register" or "push/unregister";
    public static JsonNode? Handle(string directory, string device, JsonObject request)
    {
        switch (request["method"]?.GetValue<string>())
        {
            case "push/vapid":
                return new JsonObject { ["publicKey"] = WebPush.Base64Url(RemoteTrust.VapidKey(directory).PublicKey) };
            case "push/register":
                var endpoint = request["endpoint"]?.GetValue<string>() ?? "";
                if (endpoint.Length > 1000 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new IOException("Invalid push endpoint.");
                byte[] key, auth;
                try { key = WebPush.FromBase64Url(request["p256dh"]?.GetValue<string>() ?? ""); auth = WebPush.FromBase64Url(request["auth"]?.GetValue<string>() ?? ""); }
                catch (FormatException) { throw new IOException("Invalid push keys."); }
                if (key.Length != 65 || key[0] != 4 || auth.Length != 16) throw new IOException("Invalid push keys.");
                var distributor = request["distributor"]?.GetValue<string>();
                RemoteTrust.SetPush(directory, device, new JsonObject
                {
                    ["endpoint"] = endpoint, ["p256dh"] = WebPush.Base64Url(key), ["auth"] = WebPush.Base64Url(auth),
                    ["distributor"] = distributor is { Length: <= 200 } ? distributor : null, ["updated"] = DateTimeOffset.UtcNow.ToString("O")
                });
                return JsonValue.Create(true);
            case "push/unregister":
                RemoteTrust.SetPush(directory, device, null);
                return JsonValue.Create(true);
            default: throw new IOException("Unknown push operation.");
        }
    }

    // Sends one notification to every paired device that registered for push.
    public static async Task Notify(string directory, string title, string body, string? chatId)
    {
        var payload = Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["title"] = title, ["body"] = body.Length > 300 ? body[..300] : body, ["chat"] = chatId }.ToJsonString());
        var vapid = RemoteTrust.VapidKey(directory);
        foreach (var (device, push) in RemoteTrust.PushTargets(directory))
        {
            try
            {
                var endpoint = push["endpoint"]!.GetValue<string>();
                var message = WebPush.Encrypt(payload, WebPush.FromBase64Url(push["p256dh"]!.GetValue<string>()), WebPush.FromBase64Url(push["auth"]!.GetValue<string>()));
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(message) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.TryAddWithoutValidation("TTL", "86400");
                request.Headers.TryAddWithoutValidation("Urgency", "high");
                request.Headers.TryAddWithoutValidation("Authorization", WebPush.Vapid(endpoint, vapid, Subject, DateTimeOffset.UtcNow));
                using var response = await Send(request);
                // The push server forgot this subscription; the phone re-registers when it next starts.
                if ((int)response.StatusCode is 404 or 410) RemoteTrust.SetPush(directory, device, null);
                else if (!response.IsSuccessStatusCode) AppDiagnostics.Record("Push notification", new IOException($"Push server answered {(int)response.StatusCode}."));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException or FormatException or InvalidOperationException)
            { AppDiagnostics.Record("Push notification", error); }
        }
    }
}
