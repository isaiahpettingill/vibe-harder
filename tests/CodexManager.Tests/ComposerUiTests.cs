using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class ComposerUiTests
{
    [Trait("Category", "Integration")]
    [AvaloniaTheory]
    [InlineData(1, 4, 1100)]
    [InlineData(2, 6, 640)]
    public async Task ScheduleSetupSurvivesEditingAndSavedSendIsVisibleInChat(int repeat, int interval, int width)
    {
        var directory = Directory.CreateTempSubdirectory("vibe-schedule-ui-").FullName;
        var previous = Environment.GetEnvironmentVariable("CODEX_MANAGER_DATA");
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Workspace", directory));
            store.Save(new Chat { Id = "chat", WorkspaceId = "w" });
            store.Setting(AgentProviders.CommandKey(AgentProvider.Codex, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        }
        var window = new MainWindow { Width = width }; window.Show();
        try
        {
            var composer = window.FindControl<ComposerEditor>("Composer")!;
            var clock = UiTests.Named<Button>(window, "ScheduleSend");
            var panel = UiTests.Named<Expander>(window, "ScheduledPanel");
            var flyout = Assert.IsType<Flyout>(clock.Flyout);
            var start = DateTime.Today.AddDays(2);
            var end = start.AddDays(7);
            var time = TimeSpan.FromHours(10);
            composer.Text = "Before editing";
            flyout.ShowAt(clock);
            var fields = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
            Assert.True(flyout.IsOpen);
            fields.Children.OfType<ComboBox>().Single().SelectedIndex = repeat;
            fields.Children.OfType<NumericUpDown>().Single().Value = interval;
            fields.Children.OfType<CalendarDatePicker>().First().SelectedDate = start;
            fields.Children.OfType<CalendarDatePicker>().Last().SelectedDate = end;
            fields.Children.OfType<TimePicker>().Single().SelectedTime = time;
            fields.Children.OfType<CheckBox>().Single().IsChecked = true;
            fields.Children.OfType<WrapPanel>().Single().Children.OfType<CheckBox>().First().IsChecked = false;
            flyout.Hide();

            composer.Text = "After editing";
            flyout.ShowAt(clock);
            fields = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
            Assert.Equal(repeat, fields.Children.OfType<ComboBox>().Single().SelectedIndex);
            Assert.Equal(interval, fields.Children.OfType<NumericUpDown>().Single().Value);
            Assert.Equal(start, fields.Children.OfType<CalendarDatePicker>().First().SelectedDate);
            Assert.Equal(end, fields.Children.OfType<CalendarDatePicker>().Last().SelectedDate);
            Assert.Equal(time, fields.Children.OfType<TimePicker>().Single().SelectedTime);
            Assert.True(fields.Children.OfType<CalendarDatePicker>().Last().IsVisible);
            Assert.False(fields.Children.OfType<WrapPanel>().Single().Children.OfType<CheckBox>().First().IsChecked);
            fields.Children.OfType<Button>().Single(b => b.Name == "ConfirmScheduleSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.False(flyout.IsOpen);
            Assert.True(panel.IsVisible);
            var row = Assert.IsType<Grid>(Assert.Single(UiTests.Named<StackPanel>(window, "ScheduledItems").Children));
            Assert.Contains(row.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>(), item => item.Text == "After editing");
            flyout.ShowAt(clock);
            fields = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
            Assert.Equal(0, fields.Children.OfType<ComboBox>().Single().SelectedIndex);
            flyout.Hide();
            composer.Text = "Separate composer draft";
            var edit = row.Children.OfType<IconButton>().Single(b => b.Name == "EditScheduledSend");
            var editFlyout = Assert.IsType<Flyout>(edit.Flyout);
            editFlyout.ShowAt(edit);
            fields = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(editFlyout.Content).Content);
            Assert.Equal("After editing", fields.Children.OfType<TextBox>().Single().Text);
            Assert.Equal(repeat, fields.Children.OfType<ComboBox>().Single().SelectedIndex);
            Assert.Equal(end, fields.Children.OfType<CalendarDatePicker>().Last().SelectedDate);
            var updatedTime = time.Add(TimeSpan.FromHours(2));
            fields.Children.OfType<TimePicker>().Single().SelectedTime = updatedTime;
            fields.Children.OfType<TextBox>().Single().Text = "Updated scheduled message";
            fields.Children.OfType<Button>().Single(b => b.Name == "ConfirmScheduleSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(editFlyout.IsOpen);
            Assert.Equal("Separate composer draft", composer.Text);

            row = Assert.IsType<Grid>(Assert.Single(UiTests.Named<StackPanel>(window, "ScheduledItems").Children));
            edit = row.Children.OfType<IconButton>().Single(b => b.Name == "EditScheduledSend");
            editFlyout = Assert.IsType<Flyout>(edit.Flyout);
            editFlyout.ShowAt(edit);
            fields = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(editFlyout.Content).Content);
            Assert.Equal("Updated scheduled message", fields.Children.OfType<TextBox>().Single().Text);
            Assert.Equal(updatedTime, fields.Children.OfType<TimePicker>().Single().SelectedTime);
            editFlyout.Hide();
            row.Children.OfType<IconButton>().Single(b => b.Name == "CancelScheduledSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close(); await Task.Delay(200);
            Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", previous);
        }
    }

    [Trait("Category", "Integration")]
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningChatActionStopsWithoutArchiving(bool header)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-stop-action", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Save(new Workspace("w", "Stop", directory));
            store.Save(new Chat { Id = "one", WorkspaceId = "w" });
            store.Setting(AgentProviders.CommandKey(AgentProvider.Codex, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --steering --config");
        }
        var window = new MainWindow(); window.Show();
        var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
        try
        {
            window.FindControl<ComposerEditor>("Composer")!.Text = "hang";
            UiTests.Named<Button>(window, "SendButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((!chat.Busy || !chat.Messages.Any(m => m.Text == "Working")) && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(chat.Busy);
            UiTests.Named<Button>(window, header ? "ArchiveChatButton" : "Archive_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            until = DateTime.UtcNow.AddSeconds(10);
            while (chat.Busy && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.False(chat.Busy);
            Assert.False(chat.Archived);
        }
        finally { window.Close(); await Task.Delay(200); }
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task QueueEscapeSidebarAndPanePersistence()
    {
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
            Assert.True(chat.Busy);
            composer.Text = "button queued";
            await Wait(() => Avalonia.Automation.AutomationProperties.GetName(send)?.StartsWith("Queue message") == true);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(chat.QueuedInputs);
            await Wait(() => Avalonia.Automation.AutomationProperties.GetName(send) == "Stop");
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
            await Wait(() => !chat.Busy); Assert.Empty(chat.QueuedInputs);
            UiTests.Named<Button>(window, "Rename_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = window.OwnedWindows.Single();
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(); input.Text = "Renamed";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Wait(() => chat.Title == "Renamed");
            var panes = window.FindControl<Grid>("RootPanes")!; panes.ColumnDefinitions[0].Width = new GridLength(340);
            window.UpdateLayout();
            UiTests.Named<Button>(window, "Archive_one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => chat.Archived);
        }
        finally { window.Close(); await Task.Delay(200); }
        using var saved = new Store(directory);
        Assert.Equal("Renamed", saved.Chats().Single().Title); Assert.True(saved.Chats().Single().Archived);
        Assert.Equal(340, double.Parse(saved.Setting("sidebarWidth")!, System.Globalization.CultureInfo.InvariantCulture));
    }
}
