using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private readonly RemoteDownloads remoteDownloads = new();
    private readonly StackPanel downloadRows = new() { Spacing = 10, MinWidth = 260 };
    private Flyout? downloadFlyout;

    private void InitializeDownloads()
    {
        downloadFlyout = new Flyout { Content = new ScrollViewer { Content = downloadRows, MaxHeight = 320 } };
        FlyoutBase.SetAttachedFlyout(RemoteDownloadsButton, downloadFlyout);
        RemoteDownloadsButton.Click += (_, _) => downloadFlyout.ShowAt(RemoteDownloadsButton);
        remoteDownloads.Changed += RefreshDownloads;
        RefreshDownloads();
    }

    public void ReportBrowserDownloadProgress(int id, long received, long total) => remoteDownloads.Report(id, received, total < 0 ? null : total);

    private void RefreshDownloads()
    {
        var active = remoteDownloads.Active;
        RemoteDownloadsButton.IsVisible = active.Count > 0;
        if (active.Count == 0) { downloadFlyout?.Hide(); downloadRows.Children.Clear(); return; }
        RemoteDownloadsButton.Label = active.Count == 1 ? "1 pending download" : active.Count + " pending downloads";
        RemoteDownloadsButton.Content = AppIcons.Label("download", active.Count.ToString());
        downloadRows.Children.Clear();
        foreach (var transfer in active)
        {
            var total = transfer.Total;
            var percent = total is > 0 ? Math.Clamp(transfer.Received * 100d / total.Value, 0, 100) : total == 0 ? 100 : 0;
            var title = new TextBlock { Text = transfer.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
            var detail = new TextBlock { Text = total is null ? "Connecting…" : $"{percent:0}% · {Size(transfer.Received)} / {Size(total.Value)}", FontSize = 11 };
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, IsIndeterminate = total is null, Height = 8 };
            downloadRows.Children.Add(new StackPanel { Spacing = 4, Children = { title, detail, progress } });
        }
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0} KiB" : bytes + " B";
}
