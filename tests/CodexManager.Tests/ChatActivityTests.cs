using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class ChatActivityTests
{
    [AvaloniaFact]
    public void ConnectionWorkAndPermissionHaveDistinctIndicators()
    {
        var chat = new Chat { WorkspaceId = "w", Busy = true, Status = "Connecting…" };
        var indicator = new ChatActivityIndicator(chat);
        var window = new Window { Content = indicator }; window.Show();
        try
        {
            var dot = indicator.Children.OfType<ConnectingIndicator>().Single();
            var spinner = indicator.Children.OfType<Avalonia.Controls.Shapes.Path>().Single();
            Assert.True(dot.IsVisible); Assert.False(spinner.IsVisible);
            chat.Status = "Working…"; Assert.False(dot.IsVisible); Assert.True(spinner.IsVisible);
            chat.Status = "Reconnecting…"; Assert.True(dot.IsVisible); Assert.False(spinner.IsVisible);
            chat.NeedsPermission = true; Assert.False(dot.IsVisible); Assert.False(spinner.IsVisible);
            Assert.True(indicator.Children.Single(c => c.Name == "PermissionWarning").IsVisible);
            chat.NeedsPermission = false; chat.Busy = false; Assert.False(dot.IsVisible); Assert.False(spinner.IsVisible);
        }
        finally { window.Close(); }
    }
}
