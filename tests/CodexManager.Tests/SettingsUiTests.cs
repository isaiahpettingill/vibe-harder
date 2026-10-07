using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace CodexManager.Tests;

public class SettingsUiTests
{
    [AvaloniaFact]
    public async Task TrayQuitRequiresAcceptanceAndReusesPendingConfirmation()
    {
        var directory = Directory.CreateTempSubdirectory("tray-quit-").FullName;
        var store = new Store(directory); store.Setting("remoteEnabled", "0");
        var window = new MainWindow(store); window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        var confirm = typeof(MainView).GetMethod("ConfirmTrayExit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task Request() => (Task)confirm.Invoke(window.View, null)!;
        try
        {
            window.Hide();
            var pending = Request();
            var dialog = Assert.Single(window.OwnedWindows, w => w.Title == "Quit Vibe Harder?");
            Assert.True(window.IsVisible); Assert.False(closed);
            await Request();
            Assert.Single(window.OwnedWindows, w => w.Title == "Quit Vibe Harder?");
            dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "CancelTrayQuit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending; Assert.False(closed);
            pending = Request();
            window.OwnedWindows.Single(w => w.Title == "Quit Vibe Harder?").Close();
            await pending; Assert.False(closed);
            pending = Request();
            window.OwnedWindows.Single(w => w.Title == "Quit Vibe Harder?").GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmTrayQuit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!closed && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(closed);
        }
        finally { if (!closed) window.RequestExit(); }
    }

    [AvaloniaFact]
    public async Task SavingSettingsAndTogglingReuseTrayUntilShutdown()
    {
        var directory = Directory.CreateTempSubdirectory("settings-tray-").FullName;
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory, backgroundWrites: true); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "1");
        var window = new MainWindow(store); window.Show();
        var field = typeof(MainView).GetField("tray", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        typeof(MainView).GetField("backendMaintenance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window.View,
            new BackendUpdates(store, () => [], (_, _) => false, (_, _, _) => Task.FromResult(0)));
        var original = Assert.IsType<TrayIcon>(field.GetValue(window.View));
        async Task Wait(Func<bool> check) { var until = DateTime.UtcNow.AddSeconds(5); while (!check() && DateTime.UtcNow < until) await Task.Delay(20); Assert.True(check()); }
        try
        {
            for (var i = 0; i < 3; i++)
            {
                window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = window.OwnedWindows.Single(w => w.Title == "Settings");
                Assert.True(window.IsEnabled);
                foreach (var provider in AgentProviders.All)
                {
                    var providerToggle = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == $"{provider.Provider}Enabled");
                    var initial = !AgentProviders.IsAdditional(provider.Provider);
                    Assert.Equal(initial, providerToggle.IsChecked);
                    providerToggle.IsChecked = !initial; Assert.Equal(!initial, AgentProviders.IsEnabled(store, provider.Provider));
                    providerToggle.IsChecked = initial;
                    Assert.False(dialog.GetLogicalDescendants().OfType<Expander>().Single(c => c.Name == $"{provider.Provider}Commands").IsExpanded);
                }
                Assert.Equal(3, AgentProviders.Enabled(store).Count());
                dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible); dialog.Close();
                await Wait(() => !dialog.IsVisible);
                Assert.Same(original, field.GetValue(window.View));
                Assert.Contains(original, TrayIcon.GetIcons(Application.Current!)!);
            }
            window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.OwnedWindows.Single(w => w.Title == "Settings");
            var toggle = settings.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "RunInTray");
            toggle.IsChecked = false;
            Assert.Same(original, field.GetValue(window.View)); Assert.False(original.IsVisible);
            toggle.IsChecked = true;
            var replacement = Assert.IsType<TrayIcon>(field.GetValue(window.View)); Assert.Same(original, replacement); Assert.True(replacement.IsVisible);
            settings.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.Close();
            await Wait(() => !settings.IsVisible); Assert.Same(replacement, field.GetValue(window.View));
        }
        finally
        {
            var closed = false; window.Closed += (_, _) => closed = true; window.RequestExit(); await Wait(() => closed);
            Assert.Null(field.GetValue(window.View));
        }
    }

    [AvaloniaFact]
    public void BundledNeoSpleenLoadsAndOldNerdFontSettingsStillUseIt()
    {
        var installed = FontSettings.Installed;
        try
        {
            FontSettings.Installed = _ => false;
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(FontSettings.Family(null), FontStyle.Normal, weight), out var glyphs));
                Assert.Equal("NeoSpleen", glyphs!.FamilyName); Assert.Equal(weight, glyphs.Weight);
            }
            Assert.Equal(FontSettings.Family(null), FontSettings.Family("NeoSpleen Nerd Font"));
            using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-fonts", Guid.NewGuid().ToString("N")));
            store.Setting("font:Code", "NeoSpleen Nerd Font");
            var window = new FontSettings(store); window.Show();
            try { Assert.Equal("NeoSpleen", window.GetLogicalDescendants().OfType<AutoCompleteBox>().Single(f => f.Name == "CodeFontPicker").Text); }
            finally { window.Close(); }

            // An installed Nerd Font build is preferred for the default and for old settings.
            FontSettings.Installed = name => name == "NeoSpleen Nerd Font";
            Assert.Equal("NeoSpleen Nerd Font", FontSettings.Family(null).Name);
            Assert.Equal("NeoSpleen Nerd Font", FontSettings.Family("NeoSpleen").Name);
            Assert.Equal("Consolas", FontSettings.Family("Consolas").Name);
        }
        finally { FontSettings.Installed = installed; }
    }

    [AvaloniaFact]
    public async Task FontSettingsSaveIndependentFamiliesAndDefaultToNeoSpleen()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-fonts", Guid.NewGuid().ToString("N")));
        var window = new FontSettings(store); window.Show(); await Task.Delay(100);
        var fields = window.GetLogicalDescendants().OfType<AutoCompleteBox>().ToArray();
        Assert.Equal(4, fields.Length);
        Assert.All(fields, field => Assert.Equal(FontSettings.Default(field.Name!.Replace("FontPicker", "")), field.Text));
        fields.Single(f => f.Name == "UIFontPicker").Text = "Arial";
        fields.Single(f => f.Name == "ChatFontPicker").Text = "Georgia";
        fields.Single(f => f.Name == "CodeFontPicker").Text = "Consolas";
        fields.Single(f => f.Name == "TerminalFontPicker").Text = "Courier New";
        window.GetLogicalDescendants().OfType<NumericUpDown>().Single(f => f.Name == "TerminalFontSize").Value = 17;
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveFonts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Arial", store.Setting("font:UI")); Assert.Equal("Georgia", store.Setting("font:Chat")); Assert.Equal("Consolas", store.Setting("font:Code"));
        Assert.Equal("Consolas", Assert.IsType<FontFamily>(Application.Current!.Resources["CodeFont"]).Name);
        Assert.Equal("Courier New", Assert.IsType<FontFamily>(Application.Current!.Resources["TerminalFont"]).Name);
        Assert.Equal(17d, Application.Current.Resources["TerminalFontSize"]);
        foreach (var kind in new[] { "UI", "Chat", "Code", "Terminal" }) { store.Setting("font:" + kind, FontSettings.Default(kind)); store.Setting("fontSize:" + kind, "13"); }
        FontSettings.Apply(store);
    }
    [AvaloniaFact]
    public async Task SmallTerminalSettingsSaveOnlyAnInstalledOrCustomShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-shell-ui", Guid.NewGuid().ToString("N")));
        var window = new TerminalSettings(store, [new Workspace("w", "Debian", "/tmp", "Debian")]); window.Show(); await Task.Delay(100);
        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "TerminalShell");
        Assert.All(picker.Items.OfType<ShellChoice>().Where(s => s.Id != "custom"), s => Assert.True(File.Exists(s.Executable)));
        picker.SelectedItem = picker.Items.OfType<ShellChoice>().Single(s => s.Id == "custom");
        window.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "TerminalCommand").Text = "pwsh -NoLogo";
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveTerminalSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("custom", store.Setting("terminalShell:windows")); Assert.Equal("pwsh -NoLogo", store.Setting("terminalCommand:windows"));
    }
}
