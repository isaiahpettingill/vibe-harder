using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class MobileChatLayoutTests
{
    [AvaloniaFact]
    public async Task SendingFromEarlierHistoryReturnsToLatestAndKeepsTheBottomAnchor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "send-scroll-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Test", directory));
            var chat = new Chat { Id = "c", WorkspaceId = "w" }; store.Save(chat);
            for (var i = 0; i < 150; i++) store.SaveMessage(chat, new Message { Text = "Previous message " + i });
            store.Setting(AgentProviders.CommandKey(AgentProvider.Codex, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        }
        var window = new MainWindow { Width = 420, Height = 800 }; window.Show();
        try
        {
            var list = UiTests.Named<ListBox>(window, "MessageList");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (list.ItemCount < Chat.HistoryPageSize && DateTime.UtcNow < deadline) await Task.Delay(20);
            await (Task)typeof(MainView).GetMethod("BrowseHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window.View, [false])!;
            var input = UiTests.Named<TextBox>(window, "Composer"); input.Text = "hello\nwith\nseveral\nlines"; window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!; panel.Offset = new(0, 100); window.UpdateLayout();
            await (Task)typeof(MainView).GetMethod("Send", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window.View, null)!;
            await Task.Delay(100); window.UpdateLayout();
            Assert.True(panel.IsFollowingEnd);
            Assert.InRange(Math.Abs(panel.Extent.Height - panel.Viewport.Height - panel.Offset.Y), 0, 1);
            Assert.Contains(list.Items.OfType<Message>(), m => m.Role == "user" && m.Text.StartsWith("hello"));
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }

    [AvaloniaFact]
    public async Task NewRemoteChatLoadsConfigWithoutSendingAndPassiveReadsDoNotStartIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "new-remote-config-" + Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Test", directory);
        var chat = new Chat { Id = "c", WorkspaceId = "w" }; store.Save(workspace); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config");
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        await service.Handle(new() { ["method"] = "chat", ["chatId"] = "c", ["activate"] = false });
        Assert.Null(chat.SessionId);
        await service.Handle(new() { ["method"] = "chat", ["chatId"] = "c", ["activate"] = true });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((runtime.IsReconnecting || chat.ConfigOptions.Count == 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotEmpty(chat.ConfigOptions); Assert.Empty(chat.Messages);
        var result = await service.Handle(new() { ["method"] = "chat", ["chatId"] = "c", ["activate"] = false });
        Assert.NotEmpty(result!["config"]!.AsArray());
    }

    [AvaloniaFact]
    public async Task NarrowSidebarFillsViewportShowsActionsAndClosesOnCurrentChatTap()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mobile-sidebar-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Test", directory));
            store.Save(new Chat { Id = "c", WorkspaceId = "w", Title = "Current chat" });
        }
        var window = new MainWindow { Width = 420, Height = 800 }; window.Show();
        try
        {
            await Task.Delay(100); window.UpdateLayout();
            UiTests.Named<IconButton>(window, "SidebarToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            var sidebar = UiTests.Named<Border>(window, "Sidebar");
            Assert.True(sidebar.IsVisible);
            Assert.Equal(UiTests.Named<Grid>(window, "RootPanes").Bounds.Width, sidebar.Bounds.Width, 1);
            var rename = UiTests.Named<IconButton>(window, "Rename_c");
            Assert.Equal(1, rename.Opacity);
            var row = rename.GetVisualAncestors().OfType<Grid>().First(g => g.Classes.Contains("chatRow"));
            var point = row.TranslatePoint(new Point(35, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
            Assert.False(sidebar.IsVisible);
            Assert.Equal("c", ((Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!).Id);
        }
        finally { window.RequestExit(); await Task.Delay(200); }
    }

    [AvaloniaFact]
    public void EmbeddedRemoteCanHideItsDuplicateTerminalAction()
    {
        using var view = new RemoteView(new RemoteHost("Test", "localhost", 1, "", "")) { ShowTerminalButton = false };
        Assert.False(view.ShowTerminalButton);
        view.ShowTerminalButton = true; Assert.True(view.ShowTerminalButton);
    }
}
