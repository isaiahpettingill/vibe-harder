using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public sealed class FontSettings : Window
{
    public const string DefaultName = "NeoSpleen Nerd Font";
    public static string Default(string kind) => kind is "UI" or "Chat" ? "Noto Sans" : DefaultName;
    public static FontFamily Family(string? name) => new(string.IsNullOrWhiteSpace(name) || name == DefaultName ? "avares://VibeHarder/Assets/Fonts#NeoSpleen Nerd Font" : name == "Noto Sans" ? "avares://VibeHarder/Assets/Fonts#Noto Sans" : name);
    public static void Apply(Store store)
    {
        foreach (var kind in new[] { "UI", "Chat", "Code", "Terminal" })
        {
            Application.Current!.Resources[kind + "Font"] = Family(store.Setting("font:" + kind) ?? Default(kind));
            Application.Current.Resources[kind + "FontSize"] = Size(store, kind);
        }
        Application.Current!.Resources["ToolFontSize"] = Math.Max(8, Size(store, "Code") - 2);
        Application.Current!.Resources["ContentControlThemeFontFamily"] = Family(store.Setting("font:UI") ?? Default("UI"));
    }
    public static double Size(Store store, string kind) => double.TryParse(store.Setting("fontSize:" + kind), System.Globalization.CultureInfo.InvariantCulture, out var size) && double.IsFinite(size) ? Math.Clamp(size, 8, 36) : 13;
    public FontSettings(Store store)
    {
        Title = "Fonts"; Width = 460; Height = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        var fonts = new[] { DefaultName, "Noto Sans" }.Concat(FontManager.Current.SystemFonts.Select(f => f.Name)).Distinct().Order().ToArray();
        var fields = new Dictionary<string, AutoCompleteBox>();
        var sizes = new Dictionary<string, NumericUpDown>();
        foreach (var kind in new[] { "UI", "Chat", "Code", "Terminal" })
        {
            var field = new AutoCompleteBox { Name = kind + "FontPicker", ItemsSource = fonts, Text = store.Setting("font:" + kind) ?? Default(kind), FilterMode = AutoCompleteFilterMode.Contains };
            fields[kind] = field; panel.Children.Add(new TextBlock { Text = kind + " font" }); panel.Children.Add(field);
            var size = new NumericUpDown { Name = kind + "FontSize", Minimum = 8, Maximum = 36, Increment = 1, Value = (decimal)Size(store, kind), FormatString = "0" }; sizes[kind] = size; panel.Children.Add(size);
        }
        var reset = new Button { Content = "Reset fonts and sizes" };
        reset.Click += (_, _) => { foreach (var (kind, field) in fields) field.Text = Default(kind); foreach (var size in sizes.Values) size.Value = 13; };
        var save = new Button { Name = "SaveFonts", Content = "Save" };
        save.Click += (_, _) => { foreach (var (kind, field) in fields) { store.Setting("font:" + kind, string.IsNullOrWhiteSpace(field.Text) ? Default(kind) : field.Text.Trim()); store.Setting("fontSize:" + kind, (sizes[kind].Value ?? 13).ToString(System.Globalization.CultureInfo.InvariantCulture)); } Apply(store); Close(); };
        panel.Children.Add(reset); panel.Children.Add(save); Content = panel;
    }
}
