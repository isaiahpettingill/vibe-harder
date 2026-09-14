using Avalonia;
using Avalonia.Controls;

namespace CodexManager;

public partial class MainView
{
    private Button? updateButton;
    private DesktopRelease? availableUpdate;
    private string? downloadedUpdate;
    private bool updateBusy;
    private bool restartingForUpdate;

    private void StartUpdateChecks()
    {
        if (remoteOnly || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser() || updateButton is not null) return;
        var installation = DesktopUpdater.Installation();
        updateButton = new Button { Name = "DesktopUpdateButton", Content = "Check for updates", FontSize = 11, Padding = new Thickness(8, 0), MinHeight = 20, IsEnabled = installation is not null };
        ToolTip.SetTip(updateButton, installation is null ? "Updates are available in installed release builds." : "Check for a newer release.");
        Grid.SetColumn(updateButton, 1);
        ((Grid)StatusBar.Child!).Children.Add(updateButton);
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
                if (manual) StatusText.Text = release is null ? "You’re up to date." : $"Update {release.Version} is available.";
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
}
