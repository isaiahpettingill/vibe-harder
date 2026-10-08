using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class ChatActivityTests
{
    // Each chat state shows exactly one indicator; the state is applied after the indicator is on screen.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData(true, "Connecting…", false, false, "connecting")]
    [InlineData(true, "Reconnecting…", false, false, "connecting")]
    [InlineData(true, "Working…", false, false, "spinner")]
    [InlineData(true, "Working…", true, false, "warning")]
    [InlineData(true, "Connecting…", true, false, "warning")]
    [InlineData(false, "Ready", false, true, "unread")]
    [InlineData(false, "Ready", false, false, "provider")]
    public void EachChatStateShowsOneDistinctIndicator(bool busy, string status, bool needsPermission, bool unread, string expected)
    {
        var chat = new Chat { WorkspaceId = "w" };
        var indicator = new ChatActivityIndicator(chat);
        var window = new Window { Content = indicator }; window.Show();
        try
        {
            chat.Busy = busy; chat.Status = status; chat.NeedsPermission = needsPermission; chat.HasUnreadCompletion = unread;
            var indicators = new Dictionary<string, Control>
            {
                ["connecting"] = indicator.Children.OfType<ConnectingIndicator>().Single(),
                ["spinner"] = indicator.Children.OfType<Avalonia.Controls.Shapes.Path>().Single(),
                ["warning"] = indicator.Children.Single(c => c.Name == "PermissionWarning"),
                ["unread"] = indicator.Children.OfType<Ellipse>().Single(),
                ["provider"] = indicator.Children.OfType<Image>().Single(),
            };
            Assert.Equal([expected], indicators.Where(i => i.Value.IsVisible).Select(i => i.Key));
        }
        finally { window.Close(); }
    }
}
