using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace CodexManager;

public sealed class RemoteSettings : Window
{
    public static IReadOnlyList<RemoteHost> Hosts(Store store)
    {
        try { return JsonNode.Parse(store.Setting("remoteHosts") ?? "[]")!.AsArray().Select(n => new RemoteHost(n!["name"]!.GetValue<string>(), n["address"]!.GetValue<string>(), n["port"]!.GetValue<int>(), n["key"]!.GetValue<string>(), n["fingerprint"]!.GetValue<string>())).ToArray(); }
        catch { return []; }
    }
    public static void SaveHosts(Store store, IEnumerable<RemoteHost> hosts) => store.Setting("remoteHosts", new JsonArray(hosts.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["address"] = h.Address, ["port"] = h.Port, ["key"] = h.KeyPath, ["fingerprint"] = h.Fingerprint }).ToArray()).ToJsonString());
    public RemoteSettings(Store store, string? fingerprint, Func<Task>? configureServer = null)
    {
        Title = "Remote connections"; Width = 500; Height = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = new ConnectionSettingsView(store, false, configureServer ?? (() => Task.CompletedTask));
        Content = settings;
        settings.Paired += host => Close(host);
        Closed += (_, _) => settings.Dispose();
    }
}
