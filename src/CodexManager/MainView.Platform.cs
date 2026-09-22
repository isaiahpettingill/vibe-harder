using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
    private Window? desktopWindow;
    private readonly bool remoteOnly;
    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;
    private IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;
    private Avalonia.Platform.Storage.ILauncher Launcher => TopLevel.GetTopLevel(this)!.Launcher;
    private bool compact;
    private bool sidebarOpen = true;
    private double sidebarWidth = 250;
    private bool started;
    private bool desktopStarted;
    private bool foregroundRequested;
    private ConnectionSettingsView? connectionSettings;
    private TopLevel? inputTopLevel;
    private double keyboardInset;
    private Thickness? nativeSafeArea;
    public void UpdateNativeMobileInsets(Thickness safeArea, double keyboardOcclusion)
    {
        if (!remoteOnly || closing) return;
        nativeSafeArea = safeArea; keyboardInset = Math.Max(0, keyboardOcclusion); ApplyMobileInsets();
    }
    private void SafeAreaChanged(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => ApplyMobileInsets();
    private void MobileScalingChanged(object? sender, EventArgs e)
    {
        if (nativeSafeArea is not null) { ApplyMobileInsets(); return; }
        keyboardInset = inputTopLevel?.InputPane is { State: Avalonia.Controls.Platform.InputPaneState.Open } pane ? pane.OccludedRect.Height : 0;
        ApplyMobileInsets();
    }
    private void ApplyMobileInsets()
    {
        var safe = nativeSafeArea ?? inputTopLevel?.InsetsManager?.SafeAreaPadding ?? default;
        // Keep insets on the root layout, outside the UserControl template.
        // The content must fill the viewport again when the IME closes.
        Padding = default;
        RootPanes.Margin = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, keyboardInset));
    }
    private void InputPaneChanged(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        if (!remoteOnly || nativeSafeArea is not null) return;
        keyboardInset = e.NewState == Avalonia.Controls.Platform.InputPaneState.Open ? e.EndRect.Height : 0;
        ApplyMobileInsets();
    }

    public void AttachDesktop(Window window)
    {
        desktopWindow = window;
        window.Closing += OnClosing;
        window.Activated += (_, _) => WakePresentation();
        window.Deactivated += (_, _) => SchedulePresentationSleep();
        window.AddHandler(KeyDownEvent, (_, _) => WakePresentation(), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Source, window)) return;
            var forwarded = new Avalonia.Input.KeyEventArgs { RoutedEvent = KeyDownEvent, Key = e.Key, KeyModifiers = e.KeyModifiers, PhysicalKey = e.PhysicalKey };
            RaiseEvent(forwarded); e.Handled = forwarded.Handled;
        }, RoutingStrategies.Tunnel);
        window.AddHandler(PointerPressedEvent, (_, _) => WakePresentation(), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.PropertyChanged += (_, e) => { if (e.Property == Window.IsVisibleProperty || e.Property == Window.WindowStateProperty) SchedulePresentationSleep(); };
        ConfigureTray();
        window.Opened += async (_, _) => await StartDesktop(window,
            Environment.GetCommandLineArgs().Contains("--startup") && store.Setting("runInTray") != "0" && TrayAvailable);
    }

    private async Task StartDesktop(Window window, bool startInTray)
    {
        // Opened fires again when a hidden window is shown. Startup belongs to
        // the process, not each tray activation, and must never hide it later.
        if (desktopStarted) return;
        desktopStarted = true;
        if (startInTray && !foregroundRequested) window.Hide();
        StartUpdateChecks();
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            backendUpdates = BackendMaintenance.Run(discoveryLifetime.Token);
        await ConfigureRemoteServer();
        await ConfigureWebServer();
        await OfferInterruptedChats();
    }

    private Task backendUpdates = Task.CompletedTask;
    private BackendUpdates? backendMaintenance;
    private BackendUpdates BackendMaintenance => backendMaintenance ??= new BackendUpdates(store, () => workspaces.ToArray(), (owner, provider) =>
        chats.Any(c => c.Provider == provider && runtimes.TryGetValue(c.Id, out var runtime) && runtime.HasBackendProcess && workspaces.Any(w => w.Id == c.WorkspaceId && w.Distro == owner.Distro)));

    private void InitializeLayout()
    {
        Classes.Set("touchSidebar", remoteOnly || OperatingSystem.IsAndroid());
        sidebarWidth = RootPanes.ColumnDefinitions[0].Width.Value;
        sidebarOpen = store.Setting("sidebarCollapsed") != "1";
        SidebarToggle.Click += (_, _) => { sidebarOpen = !sidebarOpen; if (!compact) store.Setting("sidebarCollapsed", sidebarOpen ? "0" : "1"); ApplyLayout(); };
        SidebarDismiss.PointerPressed += (_, _) => CollapseSidebar();
        SizeChanged += (_, _) =>
        {
            var narrow = Bounds.Width < 720;
            Classes.Set("touchSidebar", remoteOnly || OperatingSystem.IsAndroid() || narrow);
            if (narrow != compact) { compact = narrow; sidebarOpen = !narrow && store.Setting("sidebarCollapsed") != "1"; }
            ApplyLayout();
            if (remoteOnly && inputTopLevel is not null) ApplyMobileInsets();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (started) return;
            started = true;
            if (remoteOnly)
            {
                TopLevel.SetAutoSafeAreaPadding(this, false);
                HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
                HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
                inputTopLevel = TopLevel.GetTopLevel(this);
                if (inputTopLevel is not null) inputTopLevel.ScalingChanged += MobileScalingChanged;
                if (inputTopLevel?.InsetsManager is { } insets) insets.SafeAreaChanged += SafeAreaChanged;
                ApplyMobileInsets();
                if (inputTopLevel?.InputPane is { } pane) pane.StateChanged += InputPaneChanged;
                var host = RemoteSettings.Hosts(store).FirstOrDefault();
                if (host is null) ShowConnectionSettings(); else OpenRemoteHost(host);
            }
        };
        if (remoteOnly)
        {
            ToggleTerminalButton.IsVisible = false;
            TerminalDrawer.IsVisible = false;
            MobileTerminalButton.IsVisible = true;
            StatusBar.IsVisible = false; RootPanes.RowDefinitions[2].Height = new GridLength(0);
            ScrollViewer.SetVerticalScrollBarVisibility(WorkspaceScroll, Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden);
            WelcomeOpenButton.Content = "Connect to computer";
            WelcomeHeading.Text = "Connect to your computer";
            WelcomeHint.Text = "Enter its address, then the pairing number shown on your desktop.";
            StatusText.Text = "Connect to a computer to view your chats.";
        }
    }

    private async void MobileTerminalClick(object? sender, RoutedEventArgs e) { if (remoteView is not null) { CollapseSidebar(); await remoteView.ShowTerminal(); } }

    private void CollapseSidebar()
    {
        if (!compact) return;
        sidebarOpen = false; ApplyLayout();
    }

    private void ApplyLayout()
    {
        RootPanes.ColumnDefinitions[0].MinWidth = 0;
        RootPanes.ColumnDefinitions[2].MinWidth = 0;
        if (!compact && SidebarSplitter.IsVisible && RootPanes.ColumnDefinitions[0].Width.Value >= 170)
            sidebarWidth = RootPanes.ColumnDefinitions[0].Width.Value;
        RootPanes.ColumnDefinitions[0].Width = new GridLength(!compact && sidebarOpen ? sidebarWidth : 0);
        RootPanes.ColumnDefinitions[1].Width = new GridLength(!compact && sidebarOpen ? 5 : 0);
        Sidebar.IsVisible = sidebarOpen;
        SidebarSplitter.IsVisible = sidebarOpen && !compact;
        SidebarDismiss.IsVisible = sidebarOpen && compact;
        Grid.SetColumnSpan(Sidebar, compact ? 3 : 1);
        Sidebar.Width = double.NaN;
        Sidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
        SidebarToggle.Label = sidebarOpen ? "Collapse sidebar" : "Open sidebar";
        TerminalDrawer.MaxWidth = Math.Max(220, Bounds.Width - (compact ? 48 : 350));
    }

    private void ShowConnectionSettings()
    {
        if (connectionSettings is not null) return;
        var settings = new ConnectionSettingsView(store, remoteOnly, ConfigureRemoteServer, ConfigureWebServer);
        connectionSettings = settings;
        var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(done, Dock.Bottom); panel.Children.Add(done); panel.Children.Add(settings);
        var overlay = new Border { Background = Avalonia.Media.Brushes.Black, Child = panel, ZIndex = 100 };
        overlay.Bind(BackgroundProperty, this.GetResourceObservable("AppBackground"));
        Grid.SetColumnSpan(overlay, 3); Grid.SetRowSpan(overlay, 3); RootPanes.Children.Add(overlay);
        void CloseSettings() { settings.Dispose(); connectionSettings = null; RootPanes.Children.Remove(overlay); BuildWorkspaceTree(); }
        done.Click += (_, _) => CloseSettings();
        settings.Paired += host => { CloseSettings(); OpenRemoteHost(host); };
        CollapseSidebar();
    }

    public void SuspendRemotePresentation() { foreach (var view in remoteViews.Values) view.SetConnectionSuspended(true); remoteView?.SetPresentationSleeping(true); }
    public void ResumeRemotePresentation()
    {
        foreach (var view in remoteViews.Values) view.SetConnectionSuspended(false);
        remoteView?.SetPresentationSleeping(false);
        remoteView?.ReconnectHost();
    }
    public void DisposeMobile()
    {
        AppDiagnostics.RecoveryRequested -= RecoverAfterError;
        if (inputTopLevel is not null) inputTopLevel.ScalingChanged -= MobileScalingChanged;
        if (inputTopLevel?.InputPane is { } pane) pane.StateChanged -= InputPaneChanged;
        if (inputTopLevel?.InsetsManager is { } insets) insets.SafeAreaChanged -= SafeAreaChanged;
        closing = true; saveTimer.Stop(); discoveryLifetime.Cancel(); connectionSettings?.Dispose(); CloseRemoteView();
        DisposePresentationSleep(); store.Dispose();
    }

    private async Task ShowPairingCode(string device, string code, CancellationToken token, Action cancel)
    {
        if (!RemoteTrust.PairingEnabled(RemoteServer.DirectoryPath)) throw new IOException("On the host, open Settings → Connections and click Pair a new device first.");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (closing) throw new OperationCanceledException("The host is shutting down.", token);
            ShowFromTray();
            var popup = new Window { Title = "Pair a device", Width = 380, Height = 250, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true };
            popup.Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = device + " wants to connect", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Name = "DesktopPairingCode", Text = code, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Enter this number on your device. It expires in two minutes.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                }
            };
            var registration = token.Register(() => Dispatcher.UIThread.Post(popup.Close));
            popup.Closed += (_, _) => { registration.Dispose(); if (!token.IsCancellationRequested) cancel(); };
            var owner = desktopWindow!;
            while (owner.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } child) owner = child;
            popup.Show(owner); popup.Activate();
        });
    }
}
