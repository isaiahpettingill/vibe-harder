using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace CodexManager;

public sealed class RemoteSettings : Window
{
    public static IReadOnlyList<RemoteHost> Hosts(Store store)
    {
        try { return (JsonNode.Parse(store.Setting("remoteHosts") ?? "[]")!.AsArray()).Select(n => new RemoteHost(n!["name"]!.GetValue<string>(), n["address"]!.GetValue<string>(), n["port"]!.GetValue<int>(), n["key"]!.GetValue<string>(), n["fingerprint"]!.GetValue<string>())).ToArray(); }
        catch { return []; }
    }
    public RemoteSettings(Store store, string? fingerprint)
    {
        Title = "Remote hosts and server"; Width = 680; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        var enabled = new CheckBox { Content = "Allow remote clients to use this host", IsChecked = store.Setting("remoteEnabled") == "1" };
        var address = new TextBox { Text = store.Setting("remoteAddress") ?? "127.0.0.1", PlaceholderText = "Listen address (use your tailnet/LAN address)" };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = int.TryParse(store.Setting("remotePort"), out var savedPort) ? savedPort : 2222 };
        var keysFile = Path.Combine(RemoteServer.DirectoryPath, "authorized_keys");
        var keys = new TextBox { AcceptsReturn = true, MinHeight = 90, Text = File.Exists(keysFile) ? File.ReadAllText(keysFile) : "", PlaceholderText = "Authorized client public keys, one OpenSSH public key per line", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(enabled); panel.Children.Add(new TextBlock { Text = "Server listen address and port" }); panel.Children.Add(address); panel.Children.Add(port);
        panel.Children.Add(new SelectableTextBlock { Text = "Host fingerprint: " + (fingerprint ?? "Available after enabling the server and reopening these settings"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(keys);
        panel.Children.Add(new TextBlock { Text = "Connect to another host" });
        var name = new TextBox { PlaceholderText = "Display name" }; var hostAddress = new TextBox { PlaceholderText = "Hostname or IP address" };
        var hostPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 2222 };
        var key = new TextBox { PlaceholderText = "Private key file path on this device" }; var pin = new TextBox { PlaceholderText = "SHA256: fingerprint shown on the server" };
        foreach (var control in new Control[] { name, hostAddress, hostPort, key, pin }) panel.Children.Add(control);
        var generate = new Button { Content = "Generate client key and copy public key" };
        generate.Click += async (_, _) =>
        {
            Directory.CreateDirectory(RemoteServer.DirectoryPath);
            var path = Path.Combine(RemoteServer.DirectoryPath, "client_rsa");
            var publicKey = File.Exists(path + ".pub") ? File.ReadAllText(path + ".pub") : RemoteKey.Generate(path);
            key.Text = path;
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(publicKey);
        };
        panel.Children.Add(generate);
        var hosts = Hosts(store).ToList(); var list = new ListBox { ItemsSource = hosts.ToArray(), MaxHeight = 100 }; panel.Children.Add(list);
        var add = new Button { Content = "Add host" }; var remove = new Button { Content = "Remove selected host" }; var save = new Button { Content = "Save", Classes = { "accent" } }; var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(hostAddress.Text) || !File.Exists(key.Text) || pin.Text?.StartsWith("SHA256:", StringComparison.Ordinal) != true) { error.Text = "Enter a name, address, existing private key file, and the server fingerprint."; return; }
            hosts.Add(new(name.Text.Trim(), hostAddress.Text.Trim(), (int)hostPort.Value!, key.Text!, pin.Text.Trim())); list.ItemsSource = hosts.ToArray(); error.Text = "";
        };
        remove.Click += (_, _) => { if (list.SelectedItem is RemoteHost host) { hosts.Remove(host); list.ItemsSource = hosts.ToArray(); } };
        save.Click += (_, _) =>
        {
            Directory.CreateDirectory(RemoteServer.DirectoryPath); File.WriteAllText(keysFile, keys.Text ?? "");
            store.Setting("remoteEnabled", enabled.IsChecked == true ? "1" : "0"); store.Setting("remoteAddress", address.Text?.Trim() ?? "127.0.0.1"); store.Setting("remotePort", ((int)port.Value!).ToString());
            store.Setting("remoteHosts", new JsonArray(hosts.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["address"] = h.Address, ["port"] = h.Port, ["key"] = h.KeyPath, ["fingerprint"] = h.Fingerprint }).ToArray()).ToJsonString()); Close();
        };
        panel.Children.Add(add); panel.Children.Add(remove); panel.Children.Add(error); panel.Children.Add(save); Content = new ScrollViewer { Content = panel };
    }
}
