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

        return ReadLinks(targetRow).Links.FirstOrDefault(link => link.Contains(targetRow, col))?.Uri;
    }
    private sealed record LinkSpan(int FirstRow, int FirstColumn, int LastRow, int LastColumn, Uri Uri)
    {
        public bool Contains(int row, int col) => row >= FirstRow && row <= LastRow && (row != FirstRow || col >= FirstColumn) && (row != LastRow || col <= LastColumn);
    }
    private (List<LinkSpan> Links, int LastRow) ReadLinks(int targetRow)
    {
        List<LinkSpan> links = [];
        var model = Model!;
        var buffer = model.Terminal.Buffer;
        const int maxCells = 16384;
        var first = targetRow;
        var cols = model.Terminal.Cols;
        while (first > 0 && buffer.GetLine(first)?.IsWrapped == true)
        {
            if ((long)(targetRow - first + 1) * cols >= maxCells) return (links, targetRow);
            first--;
        }
        var text = new StringBuilder();
        var cells = new List<(int Row, int Column, int Width)>();
        var lastRow = first;
        for (var row = first; row < buffer.Lines.Length; row++)
        {
            var line = buffer.GetLine(row);
            if (line is null || row > first && !line.IsWrapped) break;
            lastRow = row;
            for (var col = 0; col < Math.Min(cols, line.Length); col++)
            {
                if ((long)(row - first) * cols + col >= maxCells) return (links, lastRow);
                var cell = line[col];
                if (cell.Width == 0 && col > 0 && line[col - 1].Width == 2) continue;
                var content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
                text.Append(content);
                foreach (var ch in content) cells.Add((row, col, Math.Max(1, (int)cell.Width)));
                if (text.Length > maxCells) return (links, lastRow);
            }
        }
        foreach (Match match in UrlPattern.Matches(text.ToString()))
        {
            var target = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            while (target.Length > 0 && target[^1] is ')' or ']' or '}')
            {
                var close = target[^1]; var open = close == ')' ? '(' : close == ']' ? '[' : '{';
                if (target.Count(c => c == close) <= target.Count(c => c == open)) break;
                target = target[..^1];
            }
            var length = target.Length;
            if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) target = "https://" + target;
            if (length > 0 && Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) || uri.Scheme == "mailto" && uri.OriginalString.Length > 7))
            {
                var start = cells[match.Index]; var end = cells[match.Index + length - 1];
                links.Add(new(start.Row, start.Column, end.Row, end.Column + end.Width - 1, uri));
            }
        }
        return (links, lastRow);
    }
    public IReadOnlyList<Rect> LinkUnderlines()
    {
        List<Rect> result = [];
        if (Model is not { } model || model.IsMouseModeActive) return result;
        var cell = LinkCellSize();
        var first = model.ScrollOffset;
        var end = Math.Min(model.Terminal.Buffer.Lines.Length, first + model.Terminal.Rows);
        for (var row = first; row < end;)
        {
            var group = ReadLinks(row);
            foreach (var link in group.Links)
                for (var line = Math.Max(first, link.FirstRow); line <= Math.Min(end - 1, link.LastRow); line++)
                {
                    var start = line == link.FirstRow ? link.FirstColumn : 0;
                    var stop = line == link.LastRow ? link.LastColumn + 1 : model.Terminal.Cols;
                    result.Add(new Rect(start * cell.Width, (line - first + 1) * cell.Height - 2, (stop - start) * cell.Width, 1));
                }
            row = Math.Max(row + 1, group.LastRow + 1);
        }
        return result;
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
