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
        var composer = window.FindControl<ComposerEditor>("Composer")!;
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(10); while (!check() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(check()); }
        try
        {
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Control });
            Assert.True(window.FindControl<ChatSearchBar>("ChatSearch")!.IsVisible);
            Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ChatSearchQuery").IsFocused);
            window.FindControl<ChatSearchBar>("ChatSearch")!.Close();
            composer.Text = "hang"; window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Messages.Any(m => m.Text == "Working"));
            var send = window.FindControl<Button>("SendButton")!;
            Assert.Equal("Stop", Avalonia.Automation.AutomationProperties.GetName(send));
            composer.Text = "button queued";
            await Wait(() => Avalonia.Automation.AutomationProperties.GetName(send)?.StartsWith("Queue message") == true);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(chat.QueuedInputs);
            await Wait(() => Avalonia.Automation.AutomationProperties.GetName(send) == "Stop");
            Assert.Equal("Stop", Avalonia.Automation.AutomationProperties.GetName(send));
            window.FindControl<Expander>("QueuePanel")!.IsExpanded = true; window.UpdateLayout();
            var steerQueued = window.GetVisualDescendants().OfType<IconButton>().Single(b => b.Name == "SteerQueued");
            Assert.True(steerQueued.IsVisible); steerQueued.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.QueuedInputs.Count == 0 && chat.Messages.Any(m => m.Text == "button queued"));
            var configPanel = window.FindControl<WrapPanel>("ConfigOptionsPanel")!;
            Assert.True(configPanel.IsEffectivelyEnabled);
            var modelPicker = configPanel.Children.OfType<Button>().Single(b => b.Name == "Config_model");
            var modelMenu = Assert.IsType<MenuFlyout>(modelPicker.Flyout);
            modelMenu.ShowAt(modelPicker);
            Assert.True(modelMenu.IsOpen);
            modelMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Large")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Wait(() => chat.ConfigOptions.Single(c => c.Id == "model").Current == "large");
            Assert.True(chat.Busy);
            modelMenu.Hide();
            foreach (var (id, choice, expected) in new[] { ("reasoning", "High", "high"), ("fast", "On", "true") })
            {
                await Wait(() => configPanel.Children.OfType<Button>().Any(b => b.Name == "Config_" + id) && configPanel.IsEffectivelyEnabled);
                var picker = configPanel.Children.OfType<Button>().Single(b => b.Name == "Config_" + id);
                var menu = Assert.IsType<MenuFlyout>(picker.Flyout);
                menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, choice)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Wait(() => chat.ConfigOptions.Single(c => c.Id == id).Current == expected);
                Assert.True(chat.Busy);
            }
            var tray = (TrayIcon)typeof(MainView).GetField("tray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window.View)!;
            Assert.Contains("1 agent running", tray.ToolTipText);
            Assert.Contains(tray.Menu!.Items.OfType<NativeMenuItem>(), item => item.Header?.Contains("Codex · Composer ·") == true);
            composer.Text = "queued next"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(chat.QueuedInputs); Assert.True(window.FindControl<Expander>("QueuePanel")!.IsVisible);
            composer.Text = "enter queued";
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Wait(() => chat.QueuedInputs.Count == 2);
            Assert.DoesNotContain(chat.Messages, m => m.Text == "enter queued");
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Wait(() => chat.QueuedInputs.Count == 0);
            Assert.Contains(chat.Messages, m => m.Text == "queued next\n\nenter queued");
            composer.Text = "escape queued"; send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(chat.Busy);
            composer.Text = "hang";
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Wait(() => chat.Messages.Any(m => m.Role == "user" && m.Text == "escape queued\n\nhang") && composer.Text == "");
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Wait(() => !chat.Busy); Assert.Empty(chat.QueuedInputs); Assert.Contains("no agents running", tray.ToolTipText);
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
