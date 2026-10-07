using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace CodexManager.Tests;

public class NotificationTests
{
    private static async Task Wait(Func<bool> ready, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }
    private static int Count(string chat) { lock (TestApp.Notifications) return TestApp.Notifications.Count(n => n.Chat == chat); }

    [Trait("Category", "Slow")]
    [AvaloniaFact]
    public async Task FinishedChatsNotifyUnlessTurnedOffAndOpenTheChat()
    {
        var directory = Directory.CreateTempSubdirectory("notify-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var title = "Notify " + Guid.NewGuid().ToString("N")[..8];
        store.Save(new Workspace("w", "Test", directory));
        var chat = new Chat { WorkspaceId = "w", Title = title }; store.Save(chat); store.Save(new Chat { WorkspaceId = "w", Title = "Other chat" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        Assert.True(ChatNotifications.Enabled(store));
        var window = new MainWindow(store); window.Show();
        try
        {
            var chats = UiTests.Named<ListBox>(window, "Chats_w");
            chats.SelectedItem = chats.Items.OfType<Chat>().Single(c => c.Title == title);
            var composer = window.FindControl<ComposerEditor>("Composer")!;
            var target = chats.Items.OfType<Chat>().Single(c => c.Title == title);
            // A reply in the chat the user is looking at needs no notification.
            composer.Text = "hello"; window.FindControl<IconButton>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => target.Messages.Any(m => m.Text == "Hello **world**") && !target.Busy);
            await Task.Delay(200); Assert.Equal(window.IsActive ? 0 : 1, Count(title));
            var before = Count(title);
            // One that finishes after the user moved to another chat does.
            composer.Text = "slow"; window.FindControl<IconButton>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => target.Busy);
            chats.SelectedItem = chats.Items.OfType<Chat>().Single(c => c.Title == "Other chat");
            await Wait(() => Count(title) == before + 1);
            (string Id, string Chat, string Title, Action Open) shown; lock (TestApp.Notifications) shown = TestApp.Notifications.Last(n => n.Chat == title);
            Assert.Equal("Reply ready", shown.Title); Assert.StartsWith("done:", shown.Id);
            Assert.True(ChatLinks.TryParse(ChatLinks.For(target.Id), out var linked)); Assert.Equal(target.Id, linked);
            shown.Open();
            Assert.Equal(title, Assert.IsType<Chat>(chats.SelectedItem).Title);

            store.Setting(ChatNotifications.EnabledKey, "0");
            var count = Count(title);
            composer.Text = "slow"; window.FindControl<IconButton>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => target.Busy);
            chats.SelectedItem = chats.Items.OfType<Chat>().Single(c => c.Title == "Other chat");
            await Wait(() => !target.Busy);
            await Task.Delay(300);
            Assert.Equal(count, Count(title));
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }

    [AvaloniaFact]
    public async Task OpeningFromANotificationKeepsAMaximizedWindowMaximized()
    {
        var directory = Directory.CreateTempSubdirectory("notify-window-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var window = new MainWindow(store); window.Show();
        try
        {
            window.WindowState = WindowState.Maximized;
            window.ShowFromTray();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            // From the taskbar it comes back maximized, not at its normal size.
            window.WindowState = WindowState.Minimized;
            window.ShowFromTray();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal; window.WindowState = WindowState.Minimized;
            window.ShowFromTray();
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }

    [Fact]
    public void ChatLinksOnlyAcceptChatIds()
    {
        Assert.Equal("vibeharder://chat/abc123", ChatLinks.For("abc123"));
        Assert.True(ChatLinks.TryParse("vibeharder://chat/abc123", out var id)); Assert.Equal("abc123", id);
        Assert.False(ChatLinks.TryParse("vibeharder://chat/", out _));
        Assert.False(ChatLinks.TryParse("vibeharder://chat/..%2F..%2Fx", out _));
        Assert.False(ChatLinks.TryParse("https://chat/abc", out _));
        Assert.False(ChatLinks.TryParse(@"C:\projectspp", out _));
    }
}
