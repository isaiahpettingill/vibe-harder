using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class RemoteDownloadsTests
{
    [AvaloniaFact]
    public void HeaderShowsOnlyPendingTransfersAndTheirProgress()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("download-indicator-").FullName);
        var view = new MainView(store);
        var window = new Window { Content = view, Width = 420, Height = 800 }; window.Show();
        try
        {
            var tracker = (RemoteDownloads)typeof(MainView).GetField("remoteDownloads", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
            var button = view.GetLogicalDescendants().OfType<IconButton>().Single(c => c.Name == "RemoteDownloadsButton");
            Assert.False(button.IsVisible);
            var first = tracker.Begin("/reports/first-file.zip");
            var second = tracker.Begin("/reports/second-file.zip");
            Assert.True(button.IsVisible);
            tracker.Report(first.Id, 50, 100);
            var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(button));
            var opened = false; flyout.Opened += (_, _) => opened = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(opened);
            var rows = Assert.IsType<ScrollViewer>(flyout.Content).GetLogicalDescendants().ToArray();
            Assert.Contains(rows.OfType<TextBlock>(), row => row.Text == "first-file.zip");
            Assert.Contains(rows.OfType<ProgressBar>(), bar => bar.Value == 50 && !bar.IsIndeterminate);
            Assert.Contains(rows.OfType<ProgressBar>(), bar => bar.IsIndeterminate);
            tracker.Finish(first.Id);
            Assert.True(button.IsVisible);
            tracker.Finish(second.Id);
            Assert.False(button.IsVisible);
        }
        finally { window.Close(); }
    }
}
