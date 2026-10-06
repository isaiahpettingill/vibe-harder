using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

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
        // AvaloniaEdit's own client reports 1-based columns and ignores selection requests while
        // nothing is selected. Android keyboards replace the word being composed by selecting it,
        // so every replacement inserted the new text next to the old: keystrokes doubled.
        var client = new InputClient(TextArea);
        TextArea.AddHandler(TextInputMethodClientRequestedEvent, (_, e) => { if (!IsReadOnly) e.Client = client; });
        AddHandler(PointerReleasedEvent, (_, e) => { if (e.Pointer.Type != Avalonia.Input.PointerType.Mouse) ReopenKeyboard(); }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }
    // Android shows the keyboard only when the focused input changes; TextBox also asks on each
    // tap, but TextEditor never does. Tapping a still-focused draft after the keyboard was
    // dismissed would otherwise do nothing, so hand focus over again.
    private void ReopenKeyboard()
    {
        if (!TextArea.IsFocused || TopLevel.GetTopLevel(this) is not { InputPane: { } pane } top || pane.State == Avalonia.Controls.Platform.InputPaneState.Open) return;
        var caret = CaretOffset;
        top.FocusManager?.Focus(null);
        TextArea.Focus();
        CaretOffset = caret;
    }
    // The caret's line is the surrounding text, so offsets are columns from the line start.
    internal sealed class InputClient : TextInputMethodClient
    {
        private readonly TextArea area;
        public InputClient(TextArea area)
        {
            this.area = area;
            area.Caret.PositionChanged += (_, _) => { RaiseCursorRectangleChanged(); RaiseSurroundingTextChanged(); RaiseSelectionChanged(); };
            area.SelectionChanged += (_, _) => RaiseSelectionChanged();
            area.DocumentChanged += (_, _) => RaiseSurroundingTextChanged();
        }
        public override Visual TextViewVisual => area;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => true;
        private AvaloniaEdit.Document.DocumentLine? Line => area.Document is { } document ? document.GetLineByOffset(Math.Clamp(area.Caret.Offset, 0, document.TextLength)) : null;
        public override string SurroundingText => Line is { } line ? area.Document.GetText(line) : "";
        public override TextSelection Selection
        {
            get
            {
                if (Line is not { } line) return new TextSelection(0, 0);
                int Column(int offset) => Math.Clamp(offset - line.Offset, 0, line.Length);
                var caret = Column(area.Caret.Offset);
                if (area.Selection.IsEmpty) return new TextSelection(caret, caret);
                var segment = area.Selection.SurroundingSegment;
                var (start, end) = (Column(segment.Offset), Column(segment.EndOffset));
                // Keep the anchor first, as TextBox does, so a selection made backwards stays backwards.
                return caret == start ? new TextSelection(end, start) : new TextSelection(start, end);
            }
            set
            {
                if (Line is not { } line) return;
                var start = line.Offset + Math.Clamp(value.Start, 0, line.Length);
                var end = line.Offset + Math.Clamp(value.End, 0, line.Length);
                // The caret moves first; a selection that excludes the caret would be dropped.
                area.Caret.Offset = end;
                area.Selection = AvaloniaEdit.Editing.Selection.Create(area, start, end);
            }
        }
        public override Rect CursorRectangle
        {
            get
            {
                if (area.TextView.TransformToVisual(area) is not { } transform) return default;
                var rect = area.Caret.CalculateCaretRectangle().TransformToAABB(transform);
                return rect.Translate(-area.TextView.ScrollOffset);
            }
        }
        public override void SetPreeditText(string? preeditText) { }
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
