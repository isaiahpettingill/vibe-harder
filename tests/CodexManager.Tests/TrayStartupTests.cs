using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class TrayStartupTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupHidesOnlyOnceAndExplicitActivationKeepsWindowVisible(bool activateBeforeStartup)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tray-startup-" + Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var view = new MainView(store, [], []);
        var window = new Window { Content = view };
        // Invoke the same startup path with a simulated --startup launch,
        // independently of whether the headless platform offers a system tray.
        var start = typeof(MainView).GetMethod("StartDesktop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Show();
        try
        {
            if (activateBeforeStartup) view.ShowFromTray();
            var startup = (Task)start.Invoke(view, [window, true])!;
            Assert.Equal(activateBeforeStartup, window.IsVisible);
            window.Show();
            await startup;
            Assert.True(window.IsVisible);
            for (var i = 0; i < 3; i++)
            {
                window.Hide();
                window.Show();
                await (Task)start.Invoke(view, [window, true])!;
                Assert.True(window.IsVisible);
            }
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
}
