using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class SettingsUiTests
{
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("Continue the checklist.", 1700, 600)]
    [InlineData("Finish the remaining task.\nVerify the result.", 7200, 820)]
    public async Task ClassifierPageAutosavesAndRestoresDecisionDefaults(string custom, int limit, int width)
    {
        var store = new Store(Directory.CreateTempSubdirectory("classifier-settings-").FullName);
        store.Setting("remoteEnabled", "0");
        var window = new MainWindow(store); window.Show();
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            (Window Dialog, TextBox Prompt) Open()
            {
                window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = Assert.Single(window.OwnedWindows); dialog.Width = width;
                var navigation = dialog.GetLogicalDescendants().OfType<ListBox>().Single(list => list.Name == "SettingsCategories");
                navigation.SelectedItem = "Classifier";
                var prompt = dialog.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "DecisionResumePrompt");
                Assert.True(prompt.IsEffectivelyVisible);
                return (dialog, prompt);
            }
            NumericUpDown ResponseLimit(Window dialog) => dialog.GetLogicalDescendants().OfType<NumericUpDown>()
                .Single(field => field.Name == "DecisionResponseLimit");
            var (first, input) = Open();
            var original = input.Text;
            Assert.False(string.IsNullOrWhiteSpace(original));
            var originalLimit = ResponseLimit(first).Value;
            input.Text = custom;
            ResponseLimit(first).Value = limit;
            first.Close();
            var (second, reopened) = Open();
            Assert.Equal(custom, reopened.Text);
            Assert.Equal((decimal)limit, ResponseLimit(second).Value);
            second.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ResetDecisionDefaults").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, reopened.Text);
            second.Close();
            Assert.Equal(original, DecisionSettings.Read(store).ResumePrompt);
            Assert.Equal((int)originalLimit!.Value, DecisionSettings.Read(store).ResponseLimit);
        }
        finally
        {
            window.RequestExit();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("RemotePort", "remotePort", 5440)]
    [InlineData("WebPort", "webPort", 5441)]
    public async Task ConnectionPortChangesSaveAndReconfigure(string name, string key, int port)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("connection-settings-").FullName, backgroundWrites: true);
        var configured = new TaskCompletionSource<string?>();
        Task Reconfigure() { configured.TrySetResult(store.Setting(key)); return Task.CompletedTask; }
        using var settings = new ConnectionSettingsView(store, false, Reconfigure, Reconfigure);
        var window = new Window { Content = settings };
        window.Show();
        try
        {
            foreach (var expander in settings.GetLogicalDescendants().OfType<Expander>().ToArray()) expander.IsExpanded = true;
            window.UpdateLayout();
            var numeric = settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(field => field.Name == name);
            var input = numeric.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(input.Focus());
            input.Text = "54";
            Assert.False(configured.Task.IsCompleted);
            input.Text = port.ToString();
            settings.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible).Focus();
            Assert.Equal(port.ToString(), await configured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally { window.Close(); }
    }

    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("Double Commander.exe", 600)]
    [InlineData("My File Manager.exe", 820)]
    public async Task ResetFolderLinkAppRestoresTheSystemDefault(string executable, int width)
    {
        var store = new Store(Directory.CreateTempSubdirectory("folder-link-app-").FullName);
        store.Setting("remoteEnabled", "0");
        store.Setting(FileLinks.DirectoryAppKey, Path.Combine(store.DirectoryPath, executable));
        var window = new MainWindow(store); window.Show();
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            (Window Dialog, Button Reset) Open()
            {
                window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = Assert.Single(window.OwnedWindows); dialog.Width = width;
                return (dialog, dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ResetDirectoryLinkApp"));
            }
            var (first, reset) = Open();
            Assert.True(reset.IsEnabled);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(string.IsNullOrEmpty(store.Setting(FileLinks.DirectoryAppKey)));
            first.Close();
            var (reopened, resetAfterReopen) = Open();
            Assert.False(resetAfterReopen.IsEnabled);
            reopened.Close();
        }
        finally
        {
            window.RequestExit();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

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
                var codexFullAccess = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "CodexFullAccess");
                Assert.False(codexFullAccess.IsChecked);
                codexFullAccess.IsChecked = false;
                codexFullAccess.IsChecked = true; Assert.Equal("1", store.Setting(AgentProviders.FullAccessKey));
                codexFullAccess.IsChecked = false; Assert.Equal("0", store.Setting(AgentProviders.FullAccessKey));
                var codexAutoReview = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "CodexAutoReview");
                Assert.False(codexAutoReview.IsChecked);
                codexAutoReview.IsChecked = true; Assert.Equal("1", store.Setting(AgentProviders.AutoReviewKey));
                codexAutoReview.IsChecked = false; Assert.Equal("0", store.Setting(AgentProviders.AutoReviewKey));
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
            var font = window.GetLogicalDescendants().OfType<AutoCompleteBox>().Single(f => f.Name == kind + "FontPicker");
            font.Text = family;
            font.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
            window.GetLogicalDescendants().OfType<NumericUpDown>().Single(f => f.Name == "TerminalFontSize").Value = 17;
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
        var command = window.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "TerminalCommand");
        command.Text = "pwsh -NoLogo";
        command.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal("custom", store.Setting("terminalShell:windows")); Assert.Equal("pwsh -NoLogo", store.Setting("terminalCommand:windows"));
    }
}
