using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class LifecycleTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingRunningChatHonorsAutomaticResumePreference(bool autoResume)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-lifecycle", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Setting("autoResume", autoResume ? "1" : "0");
            store.Save(new Workspace("w", "Lifecycle", directory)); store.Save(new Chat { WorkspaceId = "w" });
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        }
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(10); while (!check() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(check()); }
        var window = new MainWindow(); window.Show();
        var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
        window.FindControl<TextBox>("Composer")!.Text = "hang";
        window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => chat.Messages.Any(m => m.Text == "Working"));
        var closed = false; window.Closed += (_, _) => closed = true; window.Close(); await Wait(() => closed);
        using (var store = new Store(directory)) Assert.NotNull(store.Chats().Single().InterruptedInput);
        window = new MainWindow(); window.Show();
        try
        {
            if (autoResume)
            {
                var resumed = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
                await Wait(() => resumed.Messages.Any(m => m.Role == "user" && m.Text.StartsWith("Continue the interrupted request")) && !resumed.Busy);
                Assert.DoesNotContain(window.OwnedWindows, w => w.Title == "Resume interrupted chats");
                return;
            }
            await Wait(() => window.OwnedWindows.Any(w => w.Title == "Resume interrupted chats"));
            var recovered = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
            await Wait(() => recovered.HistoryLoaded);
            Assert.Single(recovered.Messages, m => m.Role == "user");
            var dialog = window.OwnedWindows.Single(w => w.Title == "Resume interrupted chats");
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "KeepInterruptedDrafts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(recovered.InterruptedInput); Assert.Contains("hang", recovered.Draft);
        }
        finally { closed = false; window.Closed += (_, _) => closed = true; window.Close(); await Wait(() => closed); }
    }
}
