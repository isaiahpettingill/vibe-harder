using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class PresentationRecoveryTests
{
    [AvaloniaFact]
    public async Task ReloadingFailedChatPresentationKeepsActiveAgentAndDraft()
    {
        var directory = Directory.CreateTempSubdirectory("presentation-recovery-").FullName;
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Test", directory)); store.Save(new Chat { Id = "active", WorkspaceId = "w" }); store.Save(new Chat { Id = "other", WorkspaceId = "w" }); store.Setting("chat:w", "active");
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var window = new MainWindow(store); window.Show();
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(10); while (!check() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(check()); }
        try
        {
            var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
            var composer = window.FindControl<ComposerEditor>("Composer")!; composer.Text = "hang";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Messages.Any(m => m.Text == "Working"));
            composer.Text = "Keep my unsent draft";
            var failure = new InvalidOperationException("Test rendering failure");
            await window.View.RecoverAfterError(failure);
            Assert.True(chat.Busy); Assert.Equal("Keep my unsent draft", composer.Text);
            Assert.Same(chat, UiTests.Named<ListBox>(window, "Chats_w").SelectedItem);
            await window.View.RecoverAfterError(failure); await window.View.RecoverAfterError(failure);
            Assert.True(window.IsVisible); Assert.True(chat.Busy);
            Assert.Contains(window.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Reload chat") && b.IsVisible);
            var list = UiTests.Named<ListBox>(window, "Chats_w"); list.SelectedItem = list.Items.OfType<Chat>().Single(c => c.Id == "other");
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Border>(), b => b.Name == "ChatRecoveryNotice");
            Assert.True(chat.Busy);
        }
        finally { var closed = false; window.Closed += (_, _) => closed = true; window.RequestExit(); await Wait(() => closed); }
    }
}
