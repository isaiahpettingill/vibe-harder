using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class ComposerUiTests
{
    [AvaloniaFact]
    public async Task QueueEscapeSidebarAndPanePersistence()
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")));
        var directory = Path.Combine(Path.GetTempPath(), "codex-composer", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Composer", directory));
            store.Save(new Chat { Id = "one", WorkspaceId = "w" });
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --steering --config");
        }
        var window = new MainWindow(); window.Show();
        var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
        var composer = window.FindControl<TextBox>("Composer")!;
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(10); while (!check() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(check()); }
        try
        {
            composer.Text = "hang"; window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Messages.Any(m => m.Text == "Working"));
            var tray = (TrayIcon)typeof(MainWindow).GetField("tray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            Assert.Contains("1 agent running", tray.ToolTipText);
            Assert.Contains(tray.Menu!.Items.OfType<NativeMenuItem>(), item => item.Header?.Contains("Codex · Composer ·") == true);
            composer.Text = "queued next"; composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Single(chat.QueuedInputs); Assert.True(window.FindControl<Expander>("QueuePanel")!.IsVisible);
            composer.Text = "steer now";
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Wait(() => chat.Messages.Any(m => m.Text == "steer now"));
            Assert.True(chat.Busy);
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Wait(() => !chat.Busy); Assert.Single(chat.QueuedInputs); Assert.Contains("no agents running", tray.ToolTipText);
            UiTests.Named<Button>(window, "Rename_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = window.OwnedWindows.Single();
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(); input.Text = "Renamed";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Wait(() => chat.Title == "Renamed");
            var panes = window.FindControl<Grid>("RootPanes")!; panes.ColumnDefinitions[0].Width = new GridLength(340);
            window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-composer-queue.png")), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            UiTests.Named<Button>(window, "Archive_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Archived);
        }
        finally { window.Close(); await Task.Delay(200); }
        using var saved = new Store(directory);
        Assert.Equal("Renamed", saved.Chats().Single().Title); Assert.True(saved.Chats().Single().Archived);
        Assert.Equal(340, double.Parse(saved.Setting("sidebarWidth")!, System.Globalization.CultureInfo.InvariantCulture));
    }
}
