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
    private Action? webStatusChanged;
    private IDisposable? webQr = null;
    public event Action<RemoteHost>? Paired;
    public ConnectionSettingsView(Store store, bool remoteOnly, Func<Task> configureServer, Func<Task>? configureWeb = null)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var status = new TextBlock { Name = "ConnectionStatus", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Connect to your computer", FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "On the host, open the Connections tab in Settings and click Pair a new device. " + (OperatingSystem.IsBrowser() ? "Then connect here to receive its pairing number." : "Enter its address here to receive its pairing number."), TextWrapping = TextWrapping.Wrap });
        var address = new TextBox { Name = "ConnectionAddress", Text = store.Setting("lastRemoteAddress") ?? "", PlaceholderText = "my-desktop:2222 or my-desktop.tailnet.ts.net:2222", MinHeight = 44 };
        if (OperatingSystem.IsBrowser())
        {
            var origin = new Uri(BrowserPlatform.Origin);
            address.Text = $"https://{origin.Host}:{origin.Port}";
        }
        Avalonia.Input.TextInput.TextInputOptions.SetContentType(address, Avalonia.Input.TextInput.TextInputContentType.Url);
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
                if (OperatingSystem.IsBrowser())
                {
                    var endpoint = RemotePairingSession.ParseAddress(address.Text ?? "");
                    var target = new UriBuilder("https", endpoint.Host, endpoint.Port).Uri;
                    if (target != new Uri(BrowserPlatform.Origin)) { BrowserPlatform.Navigate(target.AbsoluteUri); return; }
                }
                status.Text = "Connecting…";
                pairing = await RemotePairingSession.Start(address.Text ?? "", OperatingSystem.IsBrowser() ? "Web browser" : OperatingSystem.IsAndroid() ? "Android device" : Environment.MachineName, lifetime.Token);
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
        if (remoteOnly) { panel.Children.Add(new TextBlock { Text = "Color theme" }); panel.Children.Add(AppTheme.Picker(store)); }
        if (remoteOnly)
        {
            var allowAll = new CheckBox { Name = "AllowAllPermissions", Content = "Allow all permission prompts", IsChecked = store.Setting("allowAllPermissions") == "1" };
            allowAll.IsCheckedChanged += (_, _) => { try { store.Setting("allowAllPermissions", allowAll.IsChecked == true ? "1" : "0"); } catch (Exception error) { status.Text = error.Message; } };
            panel.Children.Add(allowAll);
        }
        if (MobileAppSecurity.Current is { } security)
        {
            var biometric = new Button { Name = "BiometricUnlock", MinHeight = 44 };
            var securityStatus = new TextBlock { Name = "BiometricUnlockStatus", TextWrapping = TextWrapping.Wrap };
            void UpdateSecurityLabel() => biometric.Content = security.Enabled ? "Turn off biometric unlock" : "Enable biometric unlock";
            UpdateSecurityLabel();
            panel.Children.Add(new TextBlock { Text = "App security", FontSize = 18 });
            panel.Children.Add(new TextBlock { Text = "Require biometrics or your device screen lock before accessing your remote sessions when opening or returning to the app. Screenshots remain available while unlocked.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(biometric);
            panel.Children.Add(securityStatus);
            biometric.Click += async (_, _) =>
            {
                biometric.IsEnabled = false;
                try { securityStatus.Text = await security.ChangeEnabled(!security.Enabled); }
                catch (Exception error) { securityStatus.Text = AppDiagnostics.Message("Could not change app lock", error); }
                finally { UpdateSecurityLabel(); biometric.IsEnabled = true; }
            };
        }
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
            var allowPairing = new Button { Name = "AllowNewPairing", Content = "Pair a new device" };
            allowPairing.Click += (_, _) => { RemoteTrust.OpenPairing(RemoteServer.DirectoryPath); status.Text = "New device pairing is enabled for two minutes. Connect from your other device now."; };
            panel.Children.Add(allowPairing);
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
            var webEnabled = new CheckBox { Name = "EnableWebAccess", Content = "Enable web access", IsChecked = store.Setting("webEnabled") == "1" };
            var webPort = new NumericUpDown { Name = "WebPort", Minimum = 1, Maximum = 65535, Value = int.TryParse(store.Setting("webPort"), out var wp) ? wp : 2223 };
            var webApply = new Button { Content = "Apply / Retry" };
            var webStatus = new TextBlock { Name = "WebAccessStatus", Text = WebAccessStatus.Text, TextWrapping = TextWrapping.Wrap };
            webStatusChanged = () => webStatus.Text = WebAccessStatus.Text;
            WebAccessStatus.Changed += webStatusChanged;
            var webUrl = new TextBox { Name = "WebAddress", Text = WebAccessStatus.Address(store), IsReadOnly = true };
            var webAddresses = new ComboBox { Name = "WebAddresses", ItemsSource = WebAccessStatus.Addresses(store), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            webAddresses.SelectionChanged += (_, _) => { if (webAddresses.SelectedItem is string address) webUrl.Text = address; };
            var openWeb = new Button { Content = "Open web UI" };
            openWeb.Click += async (_, _) => { try { if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri(webUrl.Text!)); } catch (Exception error) { status.Text = error.Message; } };
            async Task ApplyWeb()
            {
                webEnabled.IsEnabled = webApply.IsEnabled = webPort.IsEnabled = false;
                try
                {
                    store.Setting("webEnabled", webEnabled.IsChecked == true ? "1" : "0");
                    store.Setting("webPort", ((int)webPort.Value!).ToString());
                    await store.FlushAsync(); webAddresses.ItemsSource = WebAccessStatus.Addresses(store); webAddresses.SelectedIndex = 0; await (configureWeb ?? configureServer)();
                }
                catch (Exception error) { status.Text = error.Message; }
                finally { webEnabled.IsEnabled = webApply.IsEnabled = webPort.IsEnabled = true; }
            }
            webEnabled.IsCheckedChanged += async (_, _) => await ApplyWeb(); webApply.Click += async (_, _) => await ApplyWeb();
            panel.Children.Add(new Separator()); panel.Children.Add(webEnabled);
            panel.Children.Add(new TextBlock { Text = "Downloads the matching web UI from GitHub once, then serves it from this computer. Open the address on your phone over your LAN or Tailscale and accept the certificate warning before pairing.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(webStatus); panel.Children.Add(webAddresses); panel.Children.Add(webUrl); panel.Children.Add(openWeb);
#if !MOBILE_CLIENT
            var qr = new Image { Name = "WebAddressQr", Width = 160, Height = 160, HorizontalAlignment = HorizontalAlignment.Left };
            void RefreshQr()
            {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(webUrl.Text!, QRCoder.QRCodeGenerator.ECCLevel.M);
                using var png = new QRCoder.PngByteQRCode(data);
                using var bytes = new MemoryStream(png.GetGraphic(4));
                var bitmap = new Avalonia.Media.Imaging.Bitmap(bytes);
                qr.Source = bitmap; webQr?.Dispose(); webQr = bitmap;
            }
            webUrl.TextChanged += (_, _) => RefreshQr(); RefreshQr(); panel.Children.Add(qr);
#endif
            panel.Children.Add(new Expander { Header = "Web port", Content = new StackPanel { Spacing = 8, Children = { webPort, webApply } } });
        }
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    public void Dispose() { lifetime.Cancel(); pairing?.Dispose(); pairing = null; Paired = null; webQr?.Dispose(); if (webStatusChanged is not null) WebAccessStatus.Changed -= webStatusChanged; }
}
