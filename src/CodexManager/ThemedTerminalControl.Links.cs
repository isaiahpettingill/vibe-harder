using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace CodexManager;

public sealed partial class ThemedTerminalControl
{
    private static readonly Regex UrlPattern = new(@"\b(?:https?://|mailto:|www\.)[^\s<>""'\x00-\x1f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);
    private Uri? pressedLink;
    private Point linkPressPosition;
    private int linkPointer;
    private (FontFamily Font, double Size)? measuredLinkFont;
    private Size linkCellSize;

    // Match the pinned terminal renderer's cell metrics, including its fallback font.
    private Size LinkCellSize()
    {
        if (measuredLinkFont == (FontFamily, FontSize)) return linkCellSize;
        measuredLinkFont = (FontFamily, FontSize);
        foreach (var font in new[] { FontFamily, FontFamily.Default })
        {
            try
            {
                var typeface = new Typeface(font);
                if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyph)) continue;
                var shaped = TextShaper.Current.ShapeText("a", new TextShaperOptions(glyph, FontSize));
                using var run = new ShapedTextRun(shaped, new GenericTextRunProperties(typeface, FontSize));
                return linkCellSize = run.Size;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException) { }
        }
        return linkCellSize = new Size(Math.Max(FontSize * .6, 1), Math.Max(FontSize * 1.4, 1));
    }
    internal (Point Origin, Size Cell) PredictionMetrics() => (Children.FirstOrDefault()?.Bounds.TopLeft ?? default, LinkCellSize());

    public Uri? LinkAt(Point position)
    {
        if (Model is not { } model || model.IsMouseModeActive || Children.FirstOrDefault() is not { } surface) return null;
        position -= new Vector(surface.Bounds.X, surface.Bounds.Y);
        if (!new Rect(surface.Bounds.Size).Contains(position)) return null;
        var cellSize = LinkCellSize();
        if (cellSize.Width <= 0 || cellSize.Height <= 0) return null;
        var col = (int)Math.Floor(position.X / cellSize.Width);
        var row = (int)Math.Floor(position.Y / cellSize.Height);
        if (col < 0 || col >= model.Terminal.Cols || row < 0 || row >= model.Terminal.Rows) return null;
        var buffer = model.Terminal.Buffer;
        var targetRow = row + model.ScrollOffset;
        if (targetRow >= buffer.Lines.Length) return null;

        // Only inspect this logical line, joining soft wraps rather than hard newlines.
        // Bound work even when a program prints a huge unbroken line.
        const int maxCells = 16384;
        var first = targetRow;
        var cols = model.Terminal.Cols;
        while (first > 0 && buffer.GetLine(first)?.IsWrapped == true)
        {
            if ((long)(targetRow - first + 1) * cols >= maxCells) return null;
            first--;
        }
        var text = new StringBuilder();
        var targetIndex = -1;
        var visited = 0;
        for (var lineIndex = first; lineIndex < buffer.Lines.Length; lineIndex++)
        {
            var line = buffer.GetLine(lineIndex);
            if (line is null) break;
            if (lineIndex > first && !line.IsWrapped) break;
            for (var c = 0; c < Math.Min(cols, line.Length); c++)
            {
                if (++visited > maxCells) return null;
                var cell = line[c];
                if (lineIndex == targetRow && c == col) targetIndex = text.Length;
                if (cell.Width == 0 && c > 0 && line[c - 1].Width == 2)
                {
                    if (lineIndex == targetRow && c == col) targetIndex = Math.Max(0, text.Length - line[c - 1].Content.Length);
                    continue;
                }
                text.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
                if (text.Length > maxCells) return null;
            }
        }
        if (targetIndex < 0) return null;
        foreach (Match match in UrlPattern.Matches(text.ToString()))
        {
            var target = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            while (target.Length > 0 && target[^1] is ')' or ']' or '}')
            {
                var close = target[^1]; var open = close == ')' ? '(' : close == ']' ? '[' : '{';
                if (target.Count(c => c == close) <= target.Count(c => c == open)) break;
                target = target[..^1];
            }
            if (targetIndex < match.Index || targetIndex >= match.Index + target.Length) continue;
            if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) target = "https://" + target;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) || uri.Scheme == "mailto" && uri.OriginalString.Length > 7)) return uri;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        pressedLink = !HasSelection && e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? LinkAt(e.GetPosition(this)) : null;
        linkPressPosition = e.GetPosition(this); linkPointer = e.Pointer.Id;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!NearLinkPress(e.GetPosition(this))) pressedLink = null;
        base.OnPointerMoved(e);
        Cursor = !HasSelection && LinkAt(e.GetPosition(this)) is not null ? LinkCursor : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var link = pressedLink;
        pressedLink = null;
        var activate = link is not null && e.Pointer.Id == linkPointer && e.InitialPressMouseButton == MouseButton.Left &&
            NearLinkPress(e.GetPosition(this)) && !HasSelection && LinkAt(e.GetPosition(this)) == link;
        base.OnPointerReleased(e);
        if (activate) { e.Handled = true; _ = OpenTerminalLink(link!); }
    }

    private bool NearLinkPress(Point position) => Math.Abs(position.X - linkPressPosition.X) <= 6 && Math.Abs(position.Y - linkPressPosition.Y) <= 6;

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        pressedLink = null;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        pressedLink = null;
        base.OnPointerWheelChanged(e);
    }

    private async Task OpenTerminalLink(Uri uri)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            if (!await top.Launcher.LaunchUriAsync(uri)) throw new IOException("No application could open this link.");
        }
        catch (Exception error)
        {
            AppDiagnostics.Record("Open terminal link", error);
            if (TopLevel.GetTopLevel(this) is not null)
                new Flyout { Content = new TextBlock { Text = "Could not open link: " + error.Message, MaxWidth = 300, TextWrapping = TextWrapping.Wrap } }.ShowAt(this);
        }
    }
}
