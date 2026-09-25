using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;

namespace CodexManager;

// TextBox lays out the whole draft on every keystroke. TextEditor only builds
// visual lines for the visible viewport, so very long typed messages stay responsive.
// Members mirror the TextBox API the composers already use.
public sealed class ComposerEditor : TextEditor
{
    public static readonly StyledProperty<string?> PlaceholderTextProperty = AvaloniaProperty.Register<ComposerEditor, string?>(nameof(PlaceholderText));
    private bool empty = true;
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public ComposerEditor()
    {
        WordWrap = true; ShowLineNumbers = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.EnableHyperlinks = false; Options.EnableEmailHyperlinks = false; Options.AllowScrollBelowDocument = false;
        Options.HighlightCurrentLine = false; Options.EnableTextDragDrop = false;
        TextChanged += (_, _) => { if (empty != (Document.TextLength == 0)) { empty = !empty; InvalidateVisual(); } };
    }
    public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public new string Text
    {
        get => base.Text;
        // TextEditor resets the caret to 0 when replacing the document; keep TextBox behavior.
        set { base.Text = value ?? ""; CaretOffset = Document.TextLength; }
    }
    public new string SelectedText
    {
        get => base.SelectedText;
        // TextEditor selects the inserted text; TextBox leaves the caret after it.
        set { var start = SelectionStart; value ??= ""; base.SelectedText = value; Select(start + value.Length, 0); }
    }
    public int CaretIndex { get => CaretOffset; set => CaretOffset = Math.Clamp(value, 0, Document.TextLength); }
    public int SelectionEnd
    {
        get => SelectionStart + SelectionLength;
        set { var start = SelectionStart; value = Math.Clamp(value, 0, Document.TextLength); Select(Math.Min(start, value), Math.Abs(value - start)); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlaceholderTextProperty) InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!empty || string.IsNullOrEmpty(PlaceholderText) || TextArea.TextView.TranslatePoint(default, this) is not { } origin) return;
        var brush = this.FindResource("AppMuted") as IBrush ?? Brushes.Gray;
        var text = new FormattedText(PlaceholderText, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily), FontSize, brush)
        { MaxTextWidth = Math.Max(1, Bounds.Width - origin.X), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, origin);
    }
}
