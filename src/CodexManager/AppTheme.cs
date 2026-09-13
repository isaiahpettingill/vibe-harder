using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;

namespace CodexManager;

public static class AppTheme
{
    public static event Action? Changed;
    public static bool SyntaxHighlightingEnabled { get; private set; } = true;
    public static void SetSyntaxHighlighting(bool enabled)
    {
        if (SyntaxHighlightingEnabled == enabled) return;
        SyntaxHighlightingEnabled = enabled; Changed?.Invoke();
    }
    public static ThemePalette Current { get; private set; } = null!;
    // Palette provenance: catppuccin/catppuccin, ethan schoonover/solarized,
    // morhetz/gruvbox, and the classic Monokai palette.
    public static readonly ThemePalette[] All = ThemeCatalog.All;
    public static void Apply(Store store)
    {
        SyntaxHighlightingEnabled = store.Setting("syntaxHighlighting") != "0";
        Apply(All.FirstOrDefault(t => t.Name == store.Setting("theme")) ?? All[0]);
    }
    public static void Apply(ThemePalette palette)
    {
        var app = Application.Current!;
        Current = palette;
        app.RequestedThemeVariant = palette.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        void Brush(string key, string color)
        {
            // Keep brush identity: the terminal caches formatted glyph runs.
            if (app.Resources.TryGetValue(key, out var old) && old is SolidColorBrush brush) brush.Color = Color.Parse(color);
            else app.Resources[key] = new SolidColorBrush(Color.Parse(color));
        }
        Brush("AppBackground", palette.Background); Brush("AppSurface", palette.Surface);
        Brush("AppBorder", palette.Border); Brush("AppText", palette.Text);
        Brush("AppMuted", palette.Muted); Brush("AppAccent", palette.Accent);
        Brush("AppOnAccent", "#14141C");
        foreach (var key in new[] { "SystemControlForegroundBaseHighBrush", "TextFillColorPrimaryBrush" }) Brush(key, palette.Text);
        foreach (var key in new[] { "SystemControlBackgroundChromeMediumLowBrush", "SystemControlBackgroundAltHighBrush", "SystemControlBackgroundChromeLowBrush" }) Brush(key, palette.Surface);
        for (var i = 0; i < 16; i++) Brush("SvcSystems.UI.TerminalColor" + i, palette.Ansi[i]);
        Brush("SvcSystems.UI.TerminalCaretBrush", palette.Text);
        Brush("SvcSystems.UI.TerminalSelectionBrush", palette.Border);
        app.Resources["SystemAccentColor"] = Color.Parse(palette.Accent);
        Changed?.Invoke();
    }
    public static void StyleSyntax(IHighlightingDefinition? definition)
    {
        if (definition is null || Current is null) return;
        foreach (var color in definition.NamedHighlightingColors)
        {
            var name = color.Name?.ToLowerInvariant() ?? "";
            var value = name.Contains("comment") ? Current.Muted : name.Contains("string") || name.Contains("char") ? Current.Ansi[2] : name.Contains("number") || name.Contains("digit") ? Current.Ansi[5] : Current.Accent;
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(value));
            color.Background = null;
        }
    }
    public static ComboBox Picker(Store store)
    {
        var picker = new ComboBox { Name = "ThemePicker", ItemsSource = All, SelectedItem = All.FirstOrDefault(t => t.Name == store.Setting("theme")) ?? All[0], HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        picker.SelectionChanged += (_, _) =>
        {
            try { if (picker.SelectedItem is ThemePalette palette) { store.Setting("theme", palette.Name); Apply(palette); } }
            catch (Exception error) { ToolTip.SetTip(picker, AppDiagnostics.Message("Could not apply theme", error)); }
        };
        return picker;
    }
}
