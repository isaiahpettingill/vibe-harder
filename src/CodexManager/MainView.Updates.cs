using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
    private Button? updateButton;
    private DesktopRelease? availableUpdate;
    private string? downloadedUpdate;
    private bool updateBusy;
    private bool restartingForUpdate;
    private StackPanel? backendUpdatePanel;
    private LoadingSpinner? backendCheckSpinner;
    private bool backendChecking;

    private void StartUpdateChecks()
    {
        if (remoteOnly || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser() || updateButton is not null) return;
        var installation = DesktopUpdater.Installation();
        updateButton = new Button { Name = "DesktopUpdateButton", Content = "Check for updates", FontSize = 11, Padding = new Thickness(8, 0), MinHeight = 20, IsEnabled = installation is not null };
        ToolTip.SetTip(updateButton, installation is null ? "Updates are available in installed release builds." : "Check for a newer release.");
        Grid.SetColumn(updateButton, 2);
        ((Grid)StatusBar.Child!).Children.Add(updateButton);
        // Backend updates sit left of the app update and are checked separately, so a slow
        // environment never holds up the app's own update.
        backendCheckSpinner = new LoadingSpinner { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        backendUpdatePanel = new StackPanel { Name = "BackendUpdates", Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { backendCheckSpinner } };
        Grid.SetColumn(backendUpdatePanel, 1);
        ((Grid)StatusBar.Child!).Children.Add(backendUpdatePanel);
        updateButton.Click += async (_, _) =>
        {
            if (updateBusy || closing) return;
            if (availableUpdate is null) { await CheckForUpdates(true); return; }
            updateBusy = true; updateButton.IsEnabled = false;
            try
            {
                if (downloadedUpdate is null)
                {
                    updateButton.Content = "Downloading update…";
                    downloadedUpdate = await DesktopUpdater.Download(availableUpdate, discoveryLifetime.Token);
                    updateButton.Content = "Restart to update";
                    ToolTip.SetTip(updateButton, "Install the update, reopen your chats, and resume active requests.");
                }
                else
                {
                    await Task.Run(() => DesktopUpdater.InstallAfterExit(downloadedUpdate));
                    restartingForUpdate = true;
                    RequestExit();
                }
            }
            catch (OperationCanceledException) { if (!closing) updateButton.Content = "Download timed out — retry"; }
            catch (Exception error)
            {
                updateButton.Content = downloadedUpdate is null ? "Update download failed — retry" : "Update could not start — retry";
                ToolTip.SetTip(updateButton, error.Message);
            }
            finally { updateBusy = false; updateButton.IsEnabled = true; }
        };
        if (installation is not null) _ = PollUpdates();

        async Task CheckForUpdates(bool manual)
        {
            if (installation is null || updateBusy || closing || downloadedUpdate is not null) return;
            updateBusy = true; updateButton.IsEnabled = false;
            if (manual) updateButton.Content = "Checking for updates…";
            try
            {
                var release = await DesktopUpdater.Check(installation, discoveryLifetime.Token);
                if (closing) return;
                availableUpdate = release;
                updateButton.Content = release is null ? "Check for updates" : $"Download update {release.Version}";
                ToolTip.SetTip(updateButton, release is null ? "You’re up to date. Click to check again." : "A new release is available. Download now and restart when you are ready.");
                if (manual) StatusText.Text = release is null ? "The app is up to date." : $"Update {release.Version} is available.";
            }
            catch (Exception error)
            {
                if (closing) return;
                updateButton.Content = availableUpdate is null ? "Check for updates" : $"Download update {availableUpdate.Version}";
                ToolTip.SetTip(updateButton, "Update check failed: " + error.Message);
                if (manual) StatusText.Text = "Could not check for updates. Click to retry.";
                System.Diagnostics.Trace.WriteLine("Update check: " + error.Message);
            }
            finally { updateBusy = false; updateButton.IsEnabled = true; }
            if (!closing) _ = CheckBackendUpdates();
        }

        async Task PollUpdates()
        {
            var cancellation = discoveryLifetime.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                while (!cancellation.IsCancellationRequested)
                {
                    await CheckForUpdates(false);
                    await Task.Delay(TimeSpan.FromHours(4), cancellation);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
    private async Task CheckBackendUpdates()
    {
        if (backendChecking || closing || backendCheckSpinner is null) return;
        backendChecking = true; backendCheckSpinner.IsVisible = true;
        try { await BackendMaintenance.FindUpdates(update => Dispatcher.UIThread.Post(() => ShowBackendUpdate(update)), discoveryLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Backend update check", error); }
        finally { backendChecking = false; backendCheckSpinner.IsVisible = false; }
    }
    private void ShowBackendUpdate(BackendUpdates.AvailableUpdate update)
    {
        if (closing || backendUpdatePanel is null) return;
        var name = "BackendUpdate_" + (update.Environment.Distro ?? "local") + "_" + update.Provider;
        var place = update.Environment.Distro ?? (OperatingSystem.IsWindows() ? "Windows" : "this computer");
        var agent = AgentProviders.Get(update.Provider).Name;
        if (backendUpdatePanel.Children.OfType<Button>().FirstOrDefault(b => b.Name == name) is { } existing)
        { if (existing.IsEnabled) ToolTip.SetTip(existing, $"{update.Detail} is available for {agent} on {place}. Click to install it."); return; }
        var label = new TextBlock { Text = "Update " + agent, VerticalAlignment = VerticalAlignment.Center };
        var icon = PlatformIcons.For(update.Environment); icon.Width = icon.Height = 12; icon.Margin = new Thickness(6, 0, 0, 0);
        var button = new Button { Name = name, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, icon } }, FontSize = 11, Padding = new Thickness(8, 0), MinHeight = 20 };
        ToolTip.SetTip(button, $"{update.Detail} is available for {agent} on {place}. Click to install it.");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false; label.Text = "Updating " + agent + "…";
            string? problem;
            try { problem = await BackendMaintenance.Apply(update, discoveryLifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { problem = error.Message; }
            if (closing) return;
            if (problem is null) { backendUpdatePanel.Children.Remove(button); StatusText.Text = $"{agent} is up to date on {place}."; return; }
            label.Text = "Retry " + agent + " update"; ToolTip.SetTip(button, problem); button.IsEnabled = true;
            StatusText.Text = $"Could not update {agent} on {place}: {problem}";
        };
        backendUpdatePanel.Children.Add(button);
    }
}
