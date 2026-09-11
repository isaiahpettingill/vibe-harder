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
    public RemoteSettings(Store store, string? fingerprint)
    {
        var lifetime = new CancellationTokenSource(); Closed += (_, _) => lifetime.Cancel();
        Title = "Remote connections"; Width = 540; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var enabled = new CheckBox { Content = "Allow my other devices to connect", IsChecked = store.Setting("remoteEnabled") == "1" };
        var address = new TextBox { Text = store.Setting("remoteAddress") ?? "", PlaceholderText = "This computer's LAN or tailnet IP address" };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = int.TryParse(store.Setting("remotePort"), out var number) ? number : 2222 };
        var hosts = Hosts(store).ToList();
        void Save()
        {
            store.Setting("remoteEnabled", enabled.IsChecked == true ? "1" : "0");
            store.Setting("remoteAddress", string.IsNullOrWhiteSpace(address.Text) ? "127.0.0.1" : address.Text.Trim()); store.Setting("remotePort", ((int)port.Value!).ToString());
            store.Setting("remoteHosts", new JsonArray(hosts.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["address"] = h.Address, ["port"] = h.Port, ["key"] = h.KeyPath, ["fingerprint"] = h.Fingerprint }).ToArray()).ToJsonString());
        }
        panel.Children.Add(enabled); panel.Children.Add(address);
        panel.Children.Add(new Expander { Header = "Port", Content = port });
        var invite = new Button { Content = "Copy connection code and start hosting" };
        invite.Click += async (_, _) =>
        {
            try
            {
                var hostAddress = address.Text?.Trim() ?? ""; var hostPort = (int)port.Value!;
                var code = await Task.Run(() => RemoteTrust.Invite(RemoteServer.DirectoryPath, hostAddress, hostPort, Environment.MachineName));
                if (Clipboard is not { } clipboard) throw new IOException("Clipboard is unavailable.");
                await clipboard.SetTextAsync(code); enabled.IsChecked = true; Save(); await store.FlushAsync(); Close();
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        panel.Children.Add(invite);
        panel.Children.Add(new TextBlock { Text = "Paste the code on your other device within 10 minutes. Pair once; reconnect anytime.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var devices = new StackPanel { Spacing = 4 };
        async Task RefreshDevices()
        {
            var saved = await Task.Run(() => RemoteTrust.Devices(RemoteServer.DirectoryPath)); if (lifetime.IsCancellationRequested) return; devices.Children.Clear();
            foreach (var entry in saved)
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto") };
                row.Children.Add(new TextBlock { Text = entry.Value?["name"]?.GetValue<string>() ?? "Device", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var revoke = new IconButton { Icon = "remove", Label = "Revoke device access" }; Grid.SetColumn(revoke, 1); row.Children.Add(revoke);
                revoke.Click += async (_, _) => { await Task.Run(() => RemoteTrust.Revoke(RemoteServer.DirectoryPath, entry.Key)); await RefreshDevices(); };
                devices.Children.Add(row);
            }
        }
        panel.Children.Add(new Expander { Header = "Paired devices", Content = devices });
        Opened += async (_, _) => { try { await RefreshDevices(); } catch (Exception error) { status.Text = error.Message; } };
        panel.Children.Add(new Separator()); panel.Children.Add(new TextBlock { Text = "Connect to another computer" });
        var codeInput = new TextBox { PlaceholderText = "Paste its connection code", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var pair = new Button { Content = "Pair and save connection", Classes = { "accent" } };
        var list = new ListBox { ItemsSource = hosts.ToArray(), MaxHeight = 100 };
        pair.Click += async (_, _) =>
        {
            pair.IsEnabled = false;
            try
            {
                var path = Path.Combine(RemoteServer.DirectoryPath, "devices", Guid.NewGuid().ToString("N") + ".json");
                var host = await RemoteConnection.Pair(codeInput.Text ?? "", path, Environment.MachineName, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested(); hosts.Add(host); Save(); list.ItemsSource = hosts.ToArray(); codeInput.Text = ""; status.Text = "Paired. This connection is saved.";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { pair.IsEnabled = true; }
        };
        panel.Children.Add(codeInput); panel.Children.Add(pair); panel.Children.Add(list);
        var remove = new Button { Content = "Forget selected connection" };
        remove.Click += (_, _) => { if (list.SelectedItem is RemoteHost host) { hosts.Remove(host); list.ItemsSource = hosts.ToArray(); Save(); } };
        panel.Children.Add(remove); panel.Children.Add(status);
        var save = new Button { Content = "Done" }; save.Click += (_, _) => { Save(); Close(); }; panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel };
    }
}
