using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public static class ChatUsage
{
    public static void Load(Store store, Chat chat)
    {
        if (chat.UsageLoaded || chat.IsRemote) return;
        chat.UsageLoaded = true;
        var state = Read(store, "usage:" + chat.Id);
        if (state["totals"] is JsonObject totals) SetLabel(chat, totals);
        SetTokens(chat, state);
    }
    private static JsonObject Read(Store store, string key)
    {
        try { return JsonNode.Parse(store.Setting(key) ?? "{}") as JsonObject ?? new(); }
        catch (JsonException) { return new(); }
    }
    private static void SetLabel(Chat chat, JsonObject totals) => chat.CostLabel = string.Join(" · ", totals.Select(p => p.Key + " " + p.Value!.GetValue<decimal>().ToString("0.00##", CultureInfo.InvariantCulture)));
    private static void SetTokens(Chat chat, JsonObject state)
    {
        chat.ContextLabel = state["totalTokens"] is { } total ? total.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture) + " tokens" : state["used"] is { } used ? used.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture) + " context tokens" : "";
    }
    public static bool Apply(Store store, Chat chat, JsonElement update, bool replay, DateTimeOffset now)
    {
        Load(store, chat);
        var state = Read(store, "usage:" + chat.Id);
        var changed = false;
        foreach (var key in new[] { "used", "size", "totalTokens", "inputTokens", "outputTokens", "thoughtTokens", "cachedReadTokens", "cachedWriteTokens" })
            if (update.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
            { changed |= state[key]?.GetValue<long>() != count; state[key] = count; }
        if (update.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            cost.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out var total) && total >= 0 &&
            SubagentInfo.Text(cost, "currency") is { Length: 3 } currency && currency.All(char.IsAsciiLetter))
        {
            currency = currency.ToUpperInvariant();
            var totals = state["totals"] as JsonObject ?? new(); state["totals"] = totals;
            var baselines = state["baselines"] as JsonObject ?? new(); state["baselines"] = baselines;
            var key = chat.Provider + ":" + (chat.SessionId ?? chat.Id) + ":" + currency;
            var previous = baselines[key]?.GetValue<decimal>() ?? 0;
            var delta = Math.Max(0, total - previous);
            if (baselines[key] is null || delta > 0)
            {
                changed = true; baselines[key] = Math.Max(previous, total);
                totals[currency] = (totals[currency]?.GetValue<decimal>() ?? 0) + delta;
                if (!replay && delta > 0)
                {
                    var history = Read(store, "usage:daily");
                    var dayKey = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var day = history[dayKey] as JsonObject ?? new(); history[dayKey] = day;
                    day[currency] = (day[currency]?.GetValue<decimal>() ?? 0) + delta;
                    store.Setting("usage:daily", history.ToJsonString());
                }
            }
            SetLabel(chat, totals);
        }
        SetTokens(chat, state);
        if (changed) store.Setting("usage:" + chat.Id, state.ToJsonString());
        return changed;
    }
    public static string Summary(Store store, Chat chat)
    {
        var state = Read(store, "usage:" + chat.Id);
        var parts = new List<string>();
        if (chat.CostLabel.Length > 0) parts.Add("Chat cost: " + chat.CostLabel);
        if (state["used"] is { } used) parts.Add("Context: " + used.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture) + (state["size"] is { } size ? " / " + size.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture) : "") + " tokens");
        foreach (var (key, label) in new[] { ("totalTokens", "Total tokens"), ("inputTokens", "Input tokens"), ("outputTokens", "Output tokens") })
            if (state[key] is { } count) parts.Add(label + ": " + count.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }
    public static IReadOnlyList<(DateOnly Date, decimal Cost)> Daily(Store store, string currency)
    {
        return Read(store, "usage:daily").Where(p => DateOnly.TryParseExact(p.Key, "yyyy-MM-dd", out _) && p.Value is JsonObject)
            .Select(p => (DateOnly.ParseExact(p.Key, "yyyy-MM-dd"), (p.Value as JsonObject)?[currency]?.GetValue<decimal>() ?? 0)).OrderBy(p => p.Item1).ToArray();
    }
    public static string[] Currencies(Store store) => Read(store, "usage:daily").SelectMany(p => (p.Value as JsonObject)?.Select(c => c.Key) ?? []).Distinct().Order().ToArray();
}
