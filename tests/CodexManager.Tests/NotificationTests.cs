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

    [Trait("Category", "Integration")]
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

    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData(WindowState.Maximized, WindowState.Maximized, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Minimized, WindowState.Maximized)]
    [InlineData(WindowState.Normal, WindowState.Minimized, WindowState.Normal)]
    public async Task OpeningFromANotificationRestoresThePreviousWindowState(WindowState previous, WindowState current, WindowState expected)
    {
        var directory = Directory.CreateTempSubdirectory("notify-window-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var window = new MainWindow(store); window.Show();
        try
        {
            window.WindowState = previous; window.WindowState = current;
            window.ShowFromTray();
            Assert.Equal(expected, window.WindowState);
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("vibeharder://chat/abc123", "abc123")]
    [InlineData("vibeharder://chat/", null)]
    [InlineData("vibeharder://chat/..%2F..%2Fx", null)]
    [InlineData("https://chat/abc", null)]
    [InlineData(@"C:\projects\app", null)]
    [InlineData(null, null)]
    public void ChatLinksOnlyAcceptChatIds(string? link, string? expected)
    {
        Assert.Equal(expected is not null, ChatLinks.TryParse(link, out var id));
        if (expected is not null) Assert.Equal(expected, id);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("abc123")]
    [InlineData("0f8c2b9e4d6a4c1e9b7f3a2d5e6c7b8a")]
    [InlineData("remote:localhost:4567:chat-1")]
    public void ChatLinksRoundTripTheirChatId(string chatId)
    {
        Assert.True(ChatLinks.TryParse(ChatLinks.For(chatId), out var parsed));
        Assert.Equal(chatId, parsed);
    }
}
