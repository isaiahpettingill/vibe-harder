using SvcSystems.UI.Terminal;

namespace CodexManager;

public sealed class ThemedTerminalControl : TerminalControl
{
    public ThemedTerminalControl()
    {
        AttachedToVisualTree += (_, _) => { AppTheme.Changed += RefreshPalette; RefreshPalette(); };
        DetachedFromVisualTree += (_, _) => AppTheme.Changed -= RefreshPalette;
    }
    private void RefreshPalette()
    {
        // The pinned control snapshots brushes into cached FormattedText. Its
        // public font property invalidates that cache without replacing the model.
        var size = FontSize;
        SetCurrentValue(FontSizeProperty, size + .00001);
        SetCurrentValue(FontSizeProperty, size);
        Model?.UpdateDisplay();
    }
}
