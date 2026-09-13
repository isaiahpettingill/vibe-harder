using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

// Used by desktop settings and the Android single-view host.
public sealed class ConnectionSettingsView : UserControl, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private RemotePairingSession? pairing;
    public event Action<RemoteHost>? Paired;
    public ConnectionSettingsView(Store store, bool remoteOnly, Func<Task> configureServer)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var status = new TextBlock { Name = "ConnectionStatus", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Connect to your computer", FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "Enter its address. A pairing number will pop up on that computer.", TextWrapping = TextWrapping.Wrap });
        var address = new TextBox { Name = "ConnectionAddress", Text = store.Setting("lastRemoteAddress") ?? "", PlaceholderText = "my-desktop or my-desktop.tailnet.ts.net", MinHeight = 44 };
        var request = new Button { Name = "RequestPairingCode", Content = "Connect", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "accent" } };
        var code = new TextBox { Name = "PairingNumber", PlaceholderText = "Six-digit number from your desktop", MaxLength = 6, MinHeight = 44 };
        Avalonia.Input.TextInput.TextInputOptions.SetContentType(code, Avalonia.Input.TextInput.TextInputContentType.Digits);
        var finish = new Button { Name = "CompletePairing", Content = "Pair", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "accent" } };
        var numberStep = new StackPanel { IsVisible = false, Spacing = 8, Children = { code, finish } };
        panel.Children.Add(address); panel.Children.Add(request); panel.Children.Add(numberStep); panel.Children.Add(status);
        var hosts = new StackPanel { Spacing = 6 };
        request.Click += async (_, _) =>
        {
            request.IsEnabled = false; numberStep.IsVisible = false;
            pairing?.Dispose(); pairing = null;
            try
            {
                status.Text = "Connecting…";
                pairing = await RemotePairingSession.Start(address.Text ?? "", OperatingSystem.IsAndroid() ? "Android device" : Environment.MachineName, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                store.Setting("lastRemoteAddress", address.Text!.Trim());
                address.IsEnabled = false; request.Content = "Request a new code";
                numberStep.IsVisible = true; code.Text = ""; code.Focus();
                status.Text = "Enter the number shown on your desktop. It expires in two minutes.";
            }
            catch (Exception error) { status.Text = error is OperationCanceledException ? "Connection timed out. Check the address and that the desktop app is running." : error.Message; address.IsEnabled = true; }
            finally { request.IsEnabled = true; }
        };
        finish.Click += async (_, _) =>
        {
            if (pairing is null) return;
            var number = code.Text?.Trim() ?? "";
            if (number.Length != 6 || number.Any(c => c is < '0' or > '9')) { status.Text = "Enter all six digits from your desktop."; return; }
            finish.IsEnabled = request.IsEnabled = false;
            try
            {
                var path = Path.Combine(RemoteServer.DirectoryPath, "devices", Guid.NewGuid().ToString("N") + ".json");
                var host = await pairing.Complete(number, path, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                var saved = RemoteSettings.Hosts(store).Where(h => h.Address != host.Address || h.Port != host.Port).Append(host);
                RemoteSettings.SaveHosts(store, saved); await store.FlushAsync();
                status.Text = "Paired. This computer will reconnect automatically.";
                numberStep.IsVisible = false; address.IsEnabled = true;
                RefreshHosts(); Paired?.Invoke(host);
            }
            catch (Exception error) { status.Text = error.Message; numberStep.IsVisible = false; address.IsEnabled = true; }
            finally { pairing?.Dispose(); pairing = null; finish.IsEnabled = request.IsEnabled = true; }
        };
        void RefreshHosts()
        {
            hosts.Children.Clear();
            foreach (var host in RemoteSettings.Hosts(store))
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto") };
                var connect = new Button { Content = host.Name, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
                connect.Click += (_, _) => Paired?.Invoke(host); row.Children.Add(connect);
                var forget = new Button { Content = "Forget", MinHeight = 44 }; Grid.SetColumn(forget, 1); row.Children.Add(forget);
                forget.Click += (_, _) => { RemoteSettings.SaveHosts(store, RemoteSettings.Hosts(store).Where(h => h != host)); RefreshHosts(); };
                hosts.Children.Add(row);
            }
        }
        panel.Children.Add(new Separator()); panel.Children.Add(new TextBlock { Text = "Saved computers" }); panel.Children.Add(hosts); RefreshHosts();
        panel.Children.Add(new TextBlock { Text = "Color theme" }); panel.Children.Add(AppTheme.Picker(store));
        var copyLog = new Button { Content = "Copy diagnostic log" };
        copyLog.Click += async (_, _) =>
        {
            try
            {
                var path = Path.Combine(AppDiagnostics.DirectoryPath, "errors.log");
                var text = File.Exists(path) ? await File.ReadAllTextAsync(path, lifetime.Token) : "No errors recorded.";
                if (text.Length > 32 * 1024) text = text[^(32 * 1024)..];
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(text); status.Text = "Diagnostic log copied."; }
            }
            catch (Exception error) { status.Text = AppDiagnostics.Message("Could not copy diagnostics", error); }
        };
        panel.Children.Add(copyLog);
        if (!remoteOnly)
        {
            var enabled = new CheckBox { Name = "AllowRemoteConnections", Content = "Allow my other devices to connect", IsChecked = store.Setting("remoteEnabled") != "0" };
            var port = new NumericUpDown { Name = "RemotePort", Minimum = 1, Maximum = 65535, Value = int.TryParse(store.Setting("remotePort"), out var n) ? n : 2222 };
            var apply = new Button { Content = "Apply port" };
            async Task ApplyServer()
            {
                try { store.Setting("remoteEnabled", enabled.IsChecked == true ? "1" : "0"); store.Setting("remotePort", ((int)port.Value!).ToString()); await store.FlushAsync(); await configureServer(); status.Text = enabled.IsChecked == true ? "Ready for connections." : "Incoming connections disabled."; }
                catch (Exception error) { status.Text = error.Message; }
            }
            enabled.IsCheckedChanged += async (_, _) => await ApplyServer(); apply.Click += async (_, _) => await ApplyServer();
            panel.Children.Add(new Separator()); panel.Children.Add(enabled);
            panel.Children.Add(new TextBlock { Text = "Connect using this computer's name, LAN address, or Tailscale MagicDNS name.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Expander { Header = "Advanced", Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Listening port" }, port, apply } } });
            var devices = new StackPanel { Spacing = 6 };
            void RefreshDevices()
            {
                devices.Children.Clear();
                foreach (var entry in RemoteTrust.Devices(RemoteServer.DirectoryPath))
                {
                    var revoke = new Button { Content = "Revoke " + entry.Value?["name"]?.GetValue<string>() };
                    revoke.Click += (_, _) => { RemoteTrust.Revoke(RemoteServer.DirectoryPath, entry.Key); RefreshDevices(); }; devices.Children.Add(revoke);
                }
            }
            var pairedDevices = new Expander { Header = "Paired devices", Content = devices };
            pairedDevices.Expanding += (_, _) => RefreshDevices(); panel.Children.Add(pairedDevices);
        }
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    public void Dispose() { lifetime.Cancel(); pairing?.Dispose(); pairing = null; Paired = null; }
}
