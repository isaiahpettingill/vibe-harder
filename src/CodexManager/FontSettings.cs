using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public sealed class FontSettings : Window
{
    public const string DefaultName = "NeoSpleen";
    // Earlier versions bundled the Nerd Font build; settings saved then name it.
    private const string NerdFontName = "NeoSpleen Nerd Font";
    public static string Default(string kind) => kind is "UI" or "Chat" ? "Noto Sans" : DefaultName;
    private static string? Saved(Store store, string kind) => store.Setting("font:" + kind) is NerdFontName ? DefaultName : store.Setting("font:" + kind);
    // An installed NeoSpleen Nerd Font is the same face with prompt and file icons, so it wins
    // over the bundled plain build.
    public static Func<string, bool> Installed { get; set; } = name => FontManager.Current.SystemFonts.Any(f => f.Name == name);
    public static FontFamily Family(string? name) => new(string.IsNullOrWhiteSpace(name) || name is DefaultName or NerdFontName
        ? Installed(NerdFontName) ? NerdFontName : "avares://VibeHarder.UI/Assets/Fonts#NeoSpleen"
        : name == "Noto Sans" ? "avares://VibeHarder.UI/Assets/Fonts#Noto Sans" : name);
    public static void Apply(Store store)
    {
        foreach (var kind in new[] { "UI", "Chat", "Code", "Terminal" })
        {
            Application.Current!.Resources[kind + "Font"] = Family(Saved(store, kind) ?? Default(kind));
            Application.Current.Resources[kind + "FontSize"] = Size(store, kind);
        }
        Application.Current!.Resources["ToolFontSize"] = Math.Max(8, Size(store, "Code") - 2);
        Application.Current.Resources["SessionFontSize"] = Math.Max(8, Size(store, "Chat") - 2);
        Application.Current!.Resources["ContentControlThemeFontFamily"] = Family(Saved(store, "UI") ?? Default("UI"));
    }
    public static double Size(Store store, string kind) => double.TryParse(store.Setting("fontSize:" + kind), System.Globalization.CultureInfo.InvariantCulture, out var size) && double.IsFinite(size) ? Math.Clamp(size, 8, 36) : 13;
    public FontSettings(Store store)
    {
        Title = "Fonts"; Width = 460; Height = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = CreateContent(store);
    }
    public static Control CreateContent(Store store)
    {
        var panel = new StackPanel { Spacing = 8 };
        var fonts = new[] { DefaultName, "Noto Sans" }.Concat(FontManager.Current.SystemFonts.Select(f => f.Name)).Distinct().Order().ToArray();
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Save(Action change)
        {
            try { change(); Apply(store); errorText.Text = ""; }
            catch (Exception error) { errorText.Text = AppDiagnostics.Message("Could not save fonts", error); }
        }
        var fields = new Dictionary<string, AutoCompleteBox>();
        var sizes = new Dictionary<string, NumericUpDown>();
        foreach (var kind in new[] { "UI", "Chat", "Code", "Terminal" })
        {
            var field = new AutoCompleteBox { Name = kind + "FontPicker", ItemsSource = fonts, Text = Saved(store, kind) ?? Default(kind), FilterMode = AutoCompleteFilterMode.Contains };
            fields[kind] = field; panel.Children.Add(new TextBlock { Text = kind + " font" }); panel.Children.Add(field);
            void SaveFont() => Save(() => store.Setting("font:" + kind, string.IsNullOrWhiteSpace(field.Text) ? Default(kind) : field.Text.Trim()));
            field.LostFocus += (_, _) => SaveFont();
            field.SelectionChanged += (_, _) => { if (field.SelectedItem is string selected) Save(() => store.Setting("font:" + kind, selected)); };
            var size = new NumericUpDown { Name = kind + "FontSize", Minimum = 8, Maximum = 36, Increment = 1, Value = (decimal)Size(store, kind), FormatString = "0" }; sizes[kind] = size; panel.Children.Add(size);
            size.ValueChanged += (_, _) => { if (size.Value is { } value) Save(() => store.Setting("fontSize:" + kind, value.ToString(System.Globalization.CultureInfo.InvariantCulture))); };
        }
        var reset = new Button { Content = "Reset fonts and sizes" };
        reset.Click += (_, _) => Save(() =>
        {
            foreach (var (kind, field) in fields)
            {
                store.Setting("font:" + kind, Default(kind));
                store.Setting("fontSize:" + kind, "13");
                field.Text = Default(kind);
                sizes[kind].Value = 13;
            }
        });
        panel.Children.Add(reset); panel.Children.Add(errorText); return panel;
    }
}
