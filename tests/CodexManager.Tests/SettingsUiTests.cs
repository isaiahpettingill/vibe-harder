using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace CodexManager.Tests;

public class SettingsUiTests
{
    [Trait("Category", "CI")]
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
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.True(window.IsVisible); Assert.False(closed);
            await Request();
            Assert.Single(window.OwnedWindows);
            dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "CancelTrayQuit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending; Assert.False(closed);
            pending = Request();
            window.OwnedWindows.Single().Close();
            await pending; Assert.False(closed);
            pending = Request();
            window.OwnedWindows.Single().GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmTrayQuit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!closed && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(closed);
        }
        finally { if (!closed) window.RequestExit(); }
    }

    [Trait("Category", "CI")]
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
                var dialog = window.OwnedWindows.Single();
                Assert.True(window.IsEnabled);
                foreach (var provider in AgentProviders.All)
                {
                    var providerToggle = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == $"{provider.Provider}Enabled");
                    var initial = !AgentProviders.IsAdditional(provider.Provider);
                    Assert.Equal(initial, providerToggle.IsChecked);
                    providerToggle.IsChecked = !initial; Assert.Equal(!initial, AgentProviders.IsEnabled(store, provider.Provider));
                    providerToggle.IsChecked = initial;
                }
                dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible); dialog.Close();
                await Wait(() => !dialog.IsVisible);
                Assert.Same(original, field.GetValue(window.View));
                Assert.Contains(original, TrayIcon.GetIcons(Application.Current!)!);
            }
            window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.OwnedWindows.Single();
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

    // An installed NeoSpleen Nerd Font build is preferred for the default and for old settings;
    // otherwise those names resolve to the bundled NeoSpleen face in both weights.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData(false, null, "NeoSpleen")]
    [InlineData(false, "NeoSpleen", "NeoSpleen")]
    [InlineData(false, "NeoSpleen Nerd Font", "NeoSpleen")]
    [InlineData(true, null, "NeoSpleen Nerd Font")]
    [InlineData(true, "NeoSpleen", "NeoSpleen Nerd Font")]
    [InlineData(true, "NeoSpleen Nerd Font", "NeoSpleen Nerd Font")]
    [InlineData(false, "Consolas", "Consolas")]
    [InlineData(true, "Consolas", "Consolas")]
    public void FontNamesResolveToTheBundledOrInstalledNeoSpleen(bool nerdFontInstalled, string? requested, string expected)
    {
        var installed = FontSettings.Installed;
        try
        {
            FontSettings.Installed = name => nerdFontInstalled && name == "NeoSpleen Nerd Font";
            var family = FontSettings.Family(requested);
            if (nerdFontInstalled || expected != "NeoSpleen") { Assert.Equal(expected, family.Name); return; }
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out var glyphs));
                Assert.Equal(expected, glyphs!.FamilyName); Assert.Equal(weight, glyphs.Weight);
            }
        }
        finally { FontSettings.Installed = installed; }
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void OldNerdFontSettingIsEditedAsBundledNeoSpleen()
    {
        var installed = FontSettings.Installed;
        FontSettings.Installed = _ => false;
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-fonts", Guid.NewGuid().ToString("N")));
        store.Setting("font:Code", "NeoSpleen Nerd Font");
        var window = new FontSettings(store); window.Show();
        try { Assert.Equal("NeoSpleen", window.GetLogicalDescendants().OfType<AutoCompleteBox>().Single(f => f.Name == "CodeFontPicker").Text); }
        finally { window.Close(); FontSettings.Installed = installed; }
    }

    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("UI", "Arial")]
    [InlineData("Chat", "Georgia")]
    [InlineData("Code", "Consolas")]
    [InlineData("Terminal", "Courier New")]
    public async Task SavedFontFamilyIsStoredAndAppliedOnlyToItsKind(string kind, string family)
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-fonts", Guid.NewGuid().ToString("N")));
        var window = new FontSettings(store); window.Show(); await Task.Delay(100);
        try
        {
            window.GetLogicalDescendants().OfType<AutoCompleteBox>().Single(f => f.Name == kind + "FontPicker").Text = family;
            window.GetLogicalDescendants().OfType<NumericUpDown>().Single(f => f.Name == "TerminalFontSize").Value = 17;
            window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveFonts").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (var other in new[] { "UI", "Chat", "Code", "Terminal" })
            {
                var applied = Assert.IsType<FontFamily>(Application.Current!.Resources[other + "Font"]).Name;
                if (other == kind) { Assert.Equal(family, store.Setting("font:" + other)); Assert.Equal(family, applied); }
                else Assert.NotEqual(family, applied);
            }
            Assert.Equal(17d, Application.Current!.Resources["TerminalFontSize"]);
        }
        finally
        {
            window.Close();
            foreach (var other in new[] { "UI", "Chat", "Code", "Terminal" }) { store.Setting("font:" + other, FontSettings.Default(other)); store.Setting("fontSize:" + other, "13"); }
            FontSettings.Apply(store);
        }
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
