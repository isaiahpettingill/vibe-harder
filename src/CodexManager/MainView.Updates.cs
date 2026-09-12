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
        if (remoteOnly || OperatingSystem.IsAndroid() || DesktopUpdater.Installation() is not { } installation) return;
        updateButton = new Button { Name = "DesktopUpdateButton", IsVisible = false, FontSize = 11, Padding = new Thickness(8, 0), MinHeight = 20 };
        Grid.SetColumn(updateButton, 1);
        ((Grid)StatusBar.Child!).Children.Add(updateButton);
        updateButton.Click += async (_, _) =>
        {
            if (updateBusy || availableUpdate is null || closing) return;
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
        _ = PollUpdates();

        async Task PollUpdates()
        {
            var cancellation = discoveryLifetime.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var release = await DesktopUpdater.Check(installation, cancellation);
                        if (!closing && !updateBusy && downloadedUpdate is null && release is not null)
                        {
                            availableUpdate = release;
                            updateButton.Content = $"Download update {release.Version}";
                            ToolTip.SetTip(updateButton, "A new release is available. Download now and restart when you are ready.");
                            updateButton.IsVisible = true;
                        }
                    }
                    catch (Exception error) { System.Diagnostics.Trace.WriteLine("Update check: " + error.Message); }
                    await Task.Delay(TimeSpan.FromHours(4), cancellation);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
