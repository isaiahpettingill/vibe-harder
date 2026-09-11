using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CodexManager.Tests;

public class SettingsUiTests
{
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
        var artifact = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")); Directory.CreateDirectory(artifact);
        using (var screenshot = new RenderTargetBitmap(new PixelSize(530, 340))) { screenshot.Render(window); screenshot.Save(Path.Combine(artifact, "ui-terminal-settings.png"), PngBitmapEncoderOptions.Default); }
        picker.SelectedItem = picker.Items.OfType<ShellChoice>().Single(s => s.Id == "custom");
        window.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "TerminalCommand").Text = "pwsh -NoLogo";
        window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveTerminalSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("custom", store.Setting("terminalShell:windows")); Assert.Equal("pwsh -NoLogo", store.Setting("terminalCommand:windows"));
    }
}
