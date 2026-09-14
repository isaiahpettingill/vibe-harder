using System.Text.Json.Nodes;

namespace CodexManager;

public static class SidebarOrder
{
    public static T[] Apply<T>(Store store, string scope, IEnumerable<T> items, Func<T, string> id)
    {
        var values = items.ToArray();
        var saved = Read(store, scope);
        var ranks = saved.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index);
        var ordered = values.OrderBy(value => ranks.GetValueOrDefault(id(value), int.MaxValue)).ToArray();
        var next = saved.Concat(values.Select(id)).Distinct().ToArray();
        if (!next.SequenceEqual(saved)) Write(store, scope, next);
        return ordered;
    }
    public static void Move(Store store, string scope, string source, string target, bool after)
    {
        var order = Read(store, scope).ToList();
        if (source == target || !order.Contains(source) || !order.Contains(target)) return;
        order.Remove(source); order.Insert(order.IndexOf(target) + (after ? 1 : 0), source);
        Write(store, scope, order);
    }
    private static string[] Read(Store store, string scope)
    {
        try { return (JsonNode.Parse(store.Setting("sidebarOrder:" + scope) ?? "[]") as JsonArray)?.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>()).Distinct().ToArray() ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }
    private static void Write(Store store, string scope, IEnumerable<string> ids) => store.Setting("sidebarOrder:" + scope, new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()).ToJsonString());
}
