using SvcSystems.UI.Terminal;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Interactivity;

namespace CodexManager;

public sealed class ThemedTerminalControl : TerminalControl
{
    public ThemedTerminalControl()
    {
        var copy = new MenuItem { Header = "Copy" };
        var paste = new MenuItem { Header = "Paste" };
        var select = new MenuItem { Header = "Select all" };
        copy.Click += async (_, _) => await CopyText();
        paste.Click += async (_, _) => await PasteText();
        select.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy, paste, select } };
        // The terminal library raises its own event, not Avalonia's routed event.
        ContextRequested += (_, _) => { copy.IsEnabled = HasSelection; ContextMenu.Open(this); };
        SetValue(IsHoldingEnabledProperty, true);
        AddHandler(HoldingEvent, (_, e) =>
        {
            if (e.HoldingState != HoldingState.Started) return;
            if (!HasSelection && Model is { } model && Bounds.Width > 0 && Bounds.Height > 0)
                model.SelectWordOrExpression(Math.Clamp((int)(e.Position.Y / Bounds.Height * model.Terminal.Rows), 0, model.Terminal.Rows - 1),
                    Math.Clamp((int)(e.Position.X / Bounds.Width * model.Terminal.Cols), 0, model.Terminal.Cols - 1));
            copy.IsEnabled = HasSelection; ContextMenu.Open(this); e.Handled = true;
        }, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) =>
        {
            AppTheme.Changed += RefreshPalette; RefreshPalette();
            if (OperatingSystem.IsAndroid()) foreach (var bar in Children.OfType<Avalonia.Controls.Primitives.ScrollBar>()) { bar.Opacity = 0; bar.IsHitTestVisible = false; bar.Width = bar.MinWidth = bar.MaxWidth = 0; }
        };
        DetachedFromVisualTree += (_, _) => AppTheme.Changed -= RefreshPalette;
        SizeChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Model?.EnsureCaretIsVisible());
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;
        // Modifier presses must not reach the library: it clears the selection
        // on every key-down, before the subsequent C key can copy it.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var shortcut = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (shortcut && e.Key == Key.C && (HasSelection || e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        { e.Handled = true; _ = CopyText(); return; }
        if (shortcut && e.Key == Key.V)
        { e.Handled = true; _ = PasteText(); return; }
        base.OnKeyDown(e);
    }
    public async Task CopyText()
    {
        var text = string.Join("\n", (SelectedText ?? "").Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd(' ', '\t'))).TrimEnd('\n', '\r');
        if (string.IsNullOrEmpty(text)) return;
        try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); }
        catch (Exception error) { AppDiagnostics.Record("Terminal copy", error); }
    }
    public async Task PasteText()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            using var data = await clipboard.TryGetDataAsync();
            if (data is null || await data.TryGetTextAsync() is not { } text || Model is null) return;
            text = text.Replace("\r\n", "\n").Replace('\n', '\r');
            if (Model.Terminal.Engine.BracketedPasteMode) text = "\u001b[200~" + text.Replace("\u001b", "") + "\u001b[201~";
            Model.Send(text);
        }
        catch (Exception error) { AppDiagnostics.Record("Terminal paste", error); }
    }
    private void RefreshPalette()
    {
        if (AppTheme.Current is { } palette)
            SelectionBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(palette.Accent), .25);
        // The pinned control snapshots brushes into cached FormattedText. Its
        // public font property invalidates that cache without replacing the model.
        var size = FontSize;
        SetCurrentValue(FontSizeProperty, size + .00001);
        SetCurrentValue(FontSizeProperty, size);
        Model?.UpdateDisplay();
    }
}
