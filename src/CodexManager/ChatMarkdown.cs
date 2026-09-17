using System.Net;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using AvaloniaEdit;
using ColorDocument.Avalonia;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace CodexManager;

public sealed class ChatMarkdown : MarkdownScrollViewer
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextEditor, AvaloniaEdit.Highlighting.IHighlightingDefinition> syntaxDefinitions = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CTextBlock, object> decoratedBlocks = new();
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<ChatMarkdown, string>(nameof(Text), "");
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool Muted { get; set; }
    public bool SessionNotice { get; init; }
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool attached, pending;
    // The pinned renderer exposes selection only through its document. This
    // assembly is explicitly rooted for AOT along with its reflection templates.
    private static readonly FieldInfo? DocumentField = typeof(MarkdownScrollViewer).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic);
    private DocumentElement? Document => DocumentField?.GetValue(this) as DocumentElement;
    static ChatMarkdown() => TextProperty.Changed.AddClassHandler<ChatMarkdown>((view, _) => view.Refresh());
    public ChatMarkdown()
    {
        SelectionEnabled = true; Focusable = true;
        renderTimer.Tick += (_, _) => FlushRender();
        AttachedToVisualTree += (_, _) => { attached = true; FlushRender(); ScheduleDecoration(); };
        AttachedToVisualTree += (_, _) => AppTheme.Changed += RefreshSyntax;
        DetachedFromVisualTree += (_, _) => { attached = false; renderTimer.Stop(); AppTheme.Changed -= RefreshSyntax; LayoutUpdated -= DecorateAfterLayout; };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await Copy(true); }
        }, RoutingStrategies.Tunnel);
        var copy = new MenuItem { Header = "Copy selection with formatting" };
        copy.Click += async (_, _) => await Copy(true);
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy } };
    }
    private void Refresh()
    {
        pending = true;
        if (attached && !renderTimer.IsEnabled) renderTimer.Start();
    }
    private void FlushRender()
    {
        renderTimer.Stop();
        if (!attached || !pending) return;
        pending = false; Markdown = Text; ScheduleDecoration();
    }
    private void ScheduleDecoration()
    {
        LayoutUpdated -= DecorateAfterLayout;
        LayoutUpdated += DecorateAfterLayout;
    }
    private void DecorateAfterLayout(object? sender, EventArgs e)
    {
        LayoutUpdated -= DecorateAfterLayout;
        Decorate();
    }
    private void Decorate()
    {
        var codeBlocks = new List<Border>();
        foreach (var control in this.GetVisualDescendants())
        {
            if (control is Border border && border.Classes.Contains("CodeBlock")) codeBlocks.Add(border);
            if (control is not CTextBlock block) continue;
            if (decoratedBlocks.TryGetValue(block, out _)) continue;
            decoratedBlocks.Add(block, new object());
            block.Bind(CTextBlock.FontFamilyProperty, this.GetResourceObservable(Muted ? "CodeFont" : "ChatFont"));
            block.Bind(CTextBlock.FontSizeProperty, this.GetResourceObservable(SessionNotice ? "SessionFontSize" : Muted ? "ToolFontSize" : "ChatFontSize"));
            if (SessionNotice) block.FontStyle = FontStyle.Italic;
            StyleInlineCode(block.Content);
            DecorateLinks(block);
            block.Bind(CTextBlock.ForegroundProperty, this.GetResourceObservable(Muted || SessionNotice ? "AppMuted" : "AppText"));
        }
        foreach (var border in codeBlocks)
        {
            // Plain fences use an upstream horizontal scroller whose overlay
            // covers the only line. Wrap the text within the available width.
            if (border.Child is ScrollViewer { Content: TextBlock plain } plainScroll)
            {
                plainScroll.Content = null;
                plain = new SelectableTextBlock { Text = plain.Text };
                plain.TextWrapping = TextWrapping.Wrap;
                plain.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
                plain.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable(Muted ? "ToolFontSize" : "CodeFontSize"));
                plain.Margin = new Thickness(8);
                var plainCopy = new IconButton { Name = "CopyCode", Label = "Copy code", HorizontalAlignment = HorizontalAlignment.Right };
                plainCopy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(plain.Text); };
                var plainGrid = new Grid { RowDefinitions = new("Auto,Auto") };
                plainGrid.Children.Add(plainCopy); Grid.SetRow(plain, 1); plainGrid.Children.Add(plain); border.Child = plainGrid;
                continue;
            }
            if (border.Child is not Panel panel || panel is Grid) continue;
            var editor = panel.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
            if (editor is null) continue;
            // Replace the upstream overlay panel: it arranges the editor below
            // its origin without subtracting that offset, clipping the last line.
            panel.Children.Remove(editor);
            editor.Bind(TextEditor.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
            editor.Bind(TextEditor.FontSizeProperty, this.GetResourceObservable(Muted ? "ToolFontSize" : "CodeFontSize"));
            editor.Bind(TextEditor.ForegroundProperty, this.GetResourceObservable(Muted ? "AppMuted" : "AppText"));
            editor.Bind(TextEditor.BackgroundProperty, this.GetResourceObservable("AppSurface"));
            border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurface"));
            ApplyHighlighting(editor);
            editor.Padding = new Thickness(8);
            editor.WordWrap = true;
            editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            editor.MinHeight = 36;
            var copy = new IconButton { Name = "CopyCode", Label = "Copy code", HorizontalAlignment = HorizontalAlignment.Right };
            copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(editor.Text); };
            var header = new Grid { ColumnDefinitions = new("*,Auto") };
            header.Children.Add(new TextBlock { Text = editor.Tag as string, Margin = new Thickness(8, 4), FontSize = 11 });
            Grid.SetColumn(copy, 1); header.Children.Add(copy);
            var grid = new Grid { RowDefinitions = new("Auto,Auto") };
            grid.Children.Add(header); Grid.SetRow(editor, 1); grid.Children.Add(editor); border.Child = grid;
        }
    }
    private void ApplyHighlighting(TextEditor editor)
    {
        if (editor.SyntaxHighlighting is { } definition && !syntaxDefinitions.TryGetValue(editor, out _)) syntaxDefinitions.Add(editor, definition);
        if (Muted || !AppTheme.SyntaxHighlightingEnabled) editor.SyntaxHighlighting = null;
        else if (syntaxDefinitions.TryGetValue(editor, out var saved)) { AppTheme.StyleSyntax(saved); editor.SyntaxHighlighting = saved; }
    }
    private void RefreshSyntax()
    {
        ScheduleDecoration();
        foreach (var editor in this.GetVisualDescendants().OfType<TextEditor>())
        { ApplyHighlighting(editor); editor.TextArea.TextView.Redraw(); }
    }
    private void StyleInlineCode(IEnumerable<CInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is CCode)
            {
                inline.Bind(CInline.ForegroundProperty, this.GetResourceObservable("AppAccent")); inline.Bind(CInline.BackgroundProperty, this.GetResourceObservable("AppSurface"));
                inline.FontWeight = FontWeight.Normal;
                inline.Bind(CInline.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
            }
            if (inline is CSpan span) StyleInlineCode(span.Content);
        }
    }
    private void DecorateLinks(CTextBlock block)
    {
        var links = new List<(CHyperlink Link, int From, int To)>();
        var offset = 0;
        void Walk(IEnumerable<CInline> content)
        {
            foreach (var inline in content)
            {
                var from = offset;
                if (inline is CSpan span) Walk(span.Content); else offset += inline.AsString().Length;
                if (inline is CHyperlink link)
                {
                    // Handle activation ourselves so a hold can copy without
                    // the renderer also opening the link on pointer release.
                    link.Command = _ => { };
                    links.Add((link, from, offset));
                }
            }
        }
        Walk(block.Content);
        if (links.Count == 0) return;
        CHyperlink? pressed = null;
        Point start = default;
        IDisposable? hold = null;
        Point selectionStart = default;
        bool selecting = false;
        CHyperlink? At(Point point)
        {
            var index = block.CalcuatePointerFrom(point.X, point.Y).Index;
            return links.FirstOrDefault(l => index >= l.From && index < l.To).Link;
        }
        void Cancel() { pressed = null; hold?.Dispose(); hold = null; }
        block.AddHandler(PointerPressedEvent, (_, e) =>
        {
            Cancel();
            if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(block); pressed = At(start);
            if (pressed is null) return;
            if (Document is { } document && e.Pointer.Type == PointerType.Mouse)
            {
                selecting = true; selectionStart = e.GetPosition(document.Control);
                document.Select(selectionStart, selectionStart); Focus(); e.Pointer.Capture(block);
            }
            if (e.Pointer.Type == PointerType.Touch)
                hold = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    if (pressed is not { } link) return;
                    Cancel(); ShowCopyMenu([link]);
                }, TimeSpan.FromMilliseconds(500));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        block.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var delta = e.GetPosition(block) - start;
            if (delta.X * delta.X + delta.Y * delta.Y > 100) Cancel();
            if (selecting && Document is { } document) document.Select(selectionStart, e.GetPosition(document.Control));
        }, RoutingStrategies.Tunnel, true);
        block.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            var link = pressed; Cancel();
            if (selecting)
            {
                selecting = false;
                if (Document is { } document) document.Select(selectionStart, e.GetPosition(document.Control));
                e.Pointer.Capture(null); e.Handled = true;
                if (!string.IsNullOrWhiteSpace(Document?.GetSelectedText())) link = null;
            }
            if (link?.CommandParameter is { } target && ReferenceEquals(link, At(e.GetPosition(block)))) { e.Handled = true; await OpenLink(target); }
        }, RoutingStrategies.Tunnel, true);
        block.PointerCaptureLost += (_, _) => { selecting = false; Cancel(); };
        block.DetachedFromVisualTree += (_, _) => Cancel();
        void ShowCopyMenu(CHyperlink[] selected)
        {
            var menu = new ContextMenu();
            foreach (var link in selected)
            {
                var copy = new MenuItem { Header = selected.Length == 1 ? "Copy link" : "Copy link: " + link.AsString() };
                copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(link.CommandParameter); };
                menu.Items.Add(copy);
            }
            block.ContextMenu = menu; menu.Open(block);
        }
        block.ContextRequested += (_, e) =>
        {
            var choices = links.AsEnumerable();
            if (e.TryGetPosition(block, out var point))
            {
                var index = block.CalcuatePointerFrom(point.X, point.Y).Index;
                choices = links.Where(l => index >= l.From && index < l.To);
            }
            var selected = choices.ToArray();
            if (selected.Length == 0) return;
            ShowCopyMenu(selected.Select(l => l.Link).ToArray()); e.Handled = true;
        };
    }
    private async Task OpenLink(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            { if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(uri); return; }
            if (Uri.TryCreate(target, UriKind.Absolute, out uri) && !uri.IsFile) throw new IOException("Unsupported link type.");
            if (this.GetVisualAncestors().OfType<RemoteView>().FirstOrDefault() is { } remote) await remote.OpenFileLink(target);
            else if (this.GetVisualAncestors().OfType<MainView>().FirstOrDefault() is { } main) main.OpenFileLink(target);
        }
        catch (Exception error)
        {
            var popup = new Flyout { Content = new TextBlock { Text = "Could not open link: " + error.Message, MaxWidth = 300, TextWrapping = TextWrapping.Wrap } };
            popup.ShowAt(this);
        }
    }
    public async Task Copy(bool selection = false)
    {
        if (!selection) FlushRender();
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || Document is not { } document) return;
        var selected = selection ? document.GetSelectedText() : "";
        if (selection && string.IsNullOrEmpty(selected)) selected = string.Join("\n", this.GetVisualDescendants().OfType<CTextBlock>().Select(b => b.GetSelectedText()).Where(t => !string.IsNullOrEmpty(t)));
        var editorSelection = this.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault(e => e.SelectionLength > 0);
        var plainSelection = this.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault(b => !string.IsNullOrEmpty(b.SelectedText));
        if (selection && plainSelection is not null)
        { selected = plainSelection.SelectedText; await RichClipboard.Set(clipboard, selected, "<pre><code>" + Encode(selected) + "</code></pre>"); return; }
        if (selection && editorSelection is not null)
        { selected = editorSelection.SelectedText; await RichClipboard.Set(clipboard, selected, "<pre><code>" + Encode(selected) + "</code></pre>"); return; }
        if (selection && string.IsNullOrEmpty(selected)) return;
        await RichClipboard.Set(clipboard, selection ? selected : Text, Html(document, selection, selected));
    }
    public string ExportHtml() => Document is { } document ? Html(document, false, "") : "";
    private static string Html(DocumentElement element, bool selection, string selected)
    {
        if (element.Control is CTextBlock block)
        {
            if (selection && block.Selection is null) return "";
            var from = selection ? Math.Min(block.Selection!.From, block.Selection.To) : 0;
            var to = selection ? Math.Max(block.Selection!.From, block.Selection.To) : int.MaxValue;
            var offset = 0;
            return $"<p style=\"font-family:{Encode(block.FontFamily.Name)};font-size:{block.FontSize}px\">" + string.Concat(block.Content.Select(i => Inline(i, from, to, ref offset))) + "</p>";
        }
        if (element.Control.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is { } editor && !element.Children.Any())
            return !selection || selected.Contains(editor.Text, StringComparison.Ordinal) ? "<pre><code>" + Encode(editor.Text) + "</code></pre>" : "";
        var body = string.Concat(element.Children.Select(c => Html(c, selection, selected)));
        var tag = element.GetType().Name switch { "TableBlockElement" => "table", "TableCellElement" => "td", "ListBlockElement" => "ul", "ListItemElement" => "li", "BlockquoteElement" => "blockquote", _ => "div" };
        return body.Length == 0 ? "" : $"<{tag}>{body}</{tag}>";
    }
    private static string Inline(CInline inline, int from, int to, ref int offset)
    {
        string body;
        if (inline is CSpan span)
        {
            var result = new StringBuilder();
            foreach (var child in span.Content) result.Append(Inline(child, from, to, ref offset));
            body = result.ToString();
        }
        else
        {
            var text = inline.AsString(); var start = Math.Clamp(from - offset, 0, text.Length); var end = Math.Clamp(to - offset, 0, text.Length);
            body = Encode(text[start..Math.Max(start, end)]); offset += text.Length;
        }
        if (inline is CHyperlink link && Uri.TryCreate(link.CommandParameter, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return $"<a href=\"{Encode(uri.AbsoluteUri)}\">{body}</a>";
        var tag = inline switch { CBold => "strong", CItalic => "em", CCode => "code", CUnderline => "u", CStrikethrough => "s", _ => "span" };
        return $"<{tag}>{body}</{tag}>";
    }
    private static string Encode(string? value) => WebUtility.HtmlEncode(value) ?? "";
}

public static class RichClipboard
{
    public static Task Set(Avalonia.Input.Platform.IClipboard clipboard, string text, string html)
    {
        var item = new DataTransferItem(); item.Set(DataFormat.Text, text);
        item.Set(DataFormat.CreateBytesPlatformFormat(OperatingSystem.IsWindows() ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html"), Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? WindowsHtml(html) : html));
        var data = new DataTransfer(); data.Add(item); return clipboard.SetDataAsync(data);
    }
    public static string WindowsHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        const string start = "<html><body><!--StartFragment-->";
        const string end = "<!--EndFragment--></body></html>";
        var first = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var from = first + Encoding.UTF8.GetByteCount(start); var to = from + Encoding.UTF8.GetByteCount(fragment);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, header, first, to + Encoding.UTF8.GetByteCount(end), from, to) + start + fragment + end + "\0";
    }
}
