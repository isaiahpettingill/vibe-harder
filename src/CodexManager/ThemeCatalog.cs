namespace CodexManager;

public sealed record ThemePalette(string Name, bool Light, string Background, string Surface, string Border, string Text, string Muted, string Accent, string[] Ansi)
{
    public override string ToString() => Name;
}

public static class ThemeCatalog
{
    private static string[] Colors(string values) => values.Split(' ').Select(c => "#" + c).ToArray();
    public static readonly ThemePalette[] All =
    [
        new("Original", false, "#14141C", "#1C1B28", "#3B354D", "#E6E1F0", "#9693AA", "#B99AED", Colors("14141C F38BA8 A6E3A1 F9E2AF 89B4FA CBA6F7 94E2D5 BAC2DE 585B70 F38BA8 A6E3A1 F9E2AF 89B4FA CBA6F7 94E2D5 E6E1F0")),
        new("Catppuccin Mocha", false, "#1E1E2E", "#313244", "#45475A", "#CDD6F4", "#A6ADC8", "#CBA6F7", Colors("1E1E2E F38BA8 A6E3A1 F9E2AF 89B4FA F5C2E7 94E2D5 BAC2DE 585B70 F38BA8 A6E3A1 F9E2AF 89B4FA F5C2E7 94E2D5 CDD6F4")),
        new("Monokai", false, "#272822", "#34352F", "#49483E", "#F8F8F2", "#A6A69C", "#A6E22E", Colors("272822 F92672 A6E22E F4BF75 66D9EF AE81FF A1EFE4 F8F8F2 75715E F92672 A6E22E F4BF75 66D9EF AE81FF A1EFE4 F8F8F2")),
        new("Solarized Dark", false, "#002B36", "#073642", "#586E75", "#839496", "#839496", "#268BD2", Colors("002B36 DC322F 859900 B58900 268BD2 D33682 2AA198 EEE8D5 586E75 CB4B16 93A1A1 839496 6C71C4 D33682 2AA198 839496")),
        new("Gruvbox", false, "#282828", "#3C3836", "#504945", "#EBDBB2", "#A89984", "#FABD2F", Colors("282828 CC241D 98971A D79921 458588 B16286 689D6A A89984 928374 FB4934 B8BB26 FABD2F 83A598 D3869B 8EC07C EBDBB2")),
        new("Solarized Light", true, "#FDF6E3", "#EEE8D5", "#93A1A1", "#586E75", "#657B83", "#268BD2", Colors("FDF6E3 DC322F 859900 B58900 268BD2 D33682 2AA198 657B83 93A1A1 CB4B16 859900 B58900 6C71C4 D33682 2AA198 586E75"))
    ];
}
