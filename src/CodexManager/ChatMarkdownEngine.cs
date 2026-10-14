using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using Avalonia.Collections;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdown.Avalonia;
using Markdown.Avalonia.Controls;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.SyntaxHigh;
using Markdown.Avalonia.Utils;
using TextMarkerStyle = ColorDocument.Avalonia.DocumentElements.TextMarkerStyle;

namespace CodexManager;

// Keep the existing selectable Avalonia document model, but never send chat text
// through Markdown.Avalonia's regex parser. Its table detector can monopolize the
// UI thread for minutes on long non-table lines (including restored messages).
public sealed class ChatMarkdownEngine : IMarkdownEngine2
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists().UseAutoLinks().DisableHtml().Build();
    private static readonly SyntaxHighlightProvider Highlighting = new([]);
    public string AssetPathRoot { get; set; } = "";
    public ICommand? HyperlinkCommand { get; set; }
    public IContainerBlockHandler? ContainerBlockHandler { get; set; }
    public MdAvPlugins Plugins { get; set; } = new();
    public bool UseResource { get; set; }
    public CascadeDictionary CascadeResources { get; } = new();
    public IResourceDictionary Resources { get; set; } = new ResourceDictionary();

    public Control Transform(string text) => TransformElement(text).Control;
    public DocumentElement TransformElement(string text) => new DocumentRootElement(Parse(text));
    public IEnumerable<DocumentElement> ParseGamutElement(string? text, ParseStatus status) => Parse(text ?? "");
    public IEnumerable<CInline> ParseGamutInline(string? text)
    {
        var document = Markdig.Markdown.Parse(text ?? "", Pipeline);
        return document.OfType<LeafBlock>().SelectMany(block => Inlines(block.Inline)).ToArray();
    }
    private DocumentElement[] Parse(string text)
    {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        return Blocks(document).ToArray();
    }
    private IEnumerable<DocumentElement> Blocks(ContainerBlock blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    yield return new HeaderElement(Inlines(heading.Inline), heading.Level);
                    break;
                case ParagraphBlock paragraph:
                    // Linked images render below their paragraph; a paragraph of only images is replaced.
                    var images = new List<(string Url, string Alt)>();
                    var inlines = Inlines(paragraph.Inline, images);
                    if (images.Count == 0 || paragraph.Inline?.Any(i => i is not LineBreakInline && !(i is LinkInline { IsImage: true }) && !(i is LiteralInline l && l.Content.IsEmptyOrWhitespace())) == true)
                        yield return new CTextBlockElement(inlines, "Paragraph");
                    foreach (var (url, alt) in images) yield return new ImageElement(url, alt);
                    break;
                case CodeBlock code:
                    var language = (code as FencedCodeBlock)?.Info ?? "";
                    var text = code.Lines.ToString();
                    // An unclosed fence is still streaming; show its source until the diagram is complete.
                    if (code is FencedCodeBlock { ClosingFencedCharCount: > 0 } && language.Equals("mermaid", StringComparison.OrdinalIgnoreCase)) yield return new MermaidElement(text);
                    else yield return new CodeElement(language, string.IsNullOrWhiteSpace(language) ? null : Highlighting.Solve(language), text);
                    break;
                case QuoteBlock quote:
                    yield return new BlockquoteElement(Blocks(quote).ToArray());
                    break;
                case ListBlock list:
                    yield return new ListBlockElement(list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        list.OfType<ListItemBlock>().Select(item => new ListItemElement(Blocks(item).ToArray())).ToArray());
                    break;
                case Table table:
                    yield return RenderTable(table);
                    break;
                case ThematicBreakBlock:
                    yield return new UnBlockElement(new Rule(RuleType.Single));
                    break;
                case LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock container:
                    foreach (var child in Blocks(container)) yield return child;
                    break;
                case LeafBlock leaf:
                    yield return new CTextBlockElement(leaf.Inline is { } inline ? Inlines(inline) : [new CRun { Text = leaf.Lines.ToString() }]);
                    break;
            }
        }
    }
    private TableBlockElement RenderTable(Table table)
    {
        var headers = new List<TableCellElement[]>();
        var rows = new List<TableCellElement[]>();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = row.OfType<TableCell>().Select((cell, index) => new TableCellElement(Blocks(cell).ToArray())
            {
                ColSpan = Math.Max(1, cell.ColumnSpan),
                RowSpan = Math.Max(1, cell.RowSpan),
                // Pipe-table cells use -1 for ColumnIndex; their row order is authoritative.
                Horizontal = index < table.ColumnDefinitions.Count ? table.ColumnDefinitions[index].Alignment switch
                {
                    TableColumnAlign.Left => TextAlignment.Left,
                    TableColumnAlign.Center => TextAlignment.Center,
                    TableColumnAlign.Right => TextAlignment.Right,
                    _ => null
                } : null
            }).ToArray();
            (row.IsHeader ? headers : rows).Add(cells);
        }
        return new TableBlockElement(headers.ToArray(), rows.ToArray(), [], true);
    }
    private CInline[] Inlines(ContainerInline? container, List<(string Url, string Alt)>? images = null)
    {
        if (container is null) return [];
        var result = new List<CInline>();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    result.Add(new CRun { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    result.Add(new CCode([new CRun { Text = code.Content }]));
                    break;
                case LineBreakInline line:
                    result.Add(line.IsHard ? new CLineBreak() : new CRun { Text = " " });
                    break;
                case EmphasisInline emphasis:
                    var children = Inlines(emphasis, images);
                    result.Add(emphasis.DelimiterChar == '~' ? new CStrikethrough(children)
                        : emphasis.DelimiterCount == 2 ? new CBold(children) : new CItalic(children));
                    break;
                case LinkInline link:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    if (link.IsImage)
                    {
                        var alt = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                        images?.Add((url, alt));
                        result.Add(Link([new CRun { Text = alt.Length > 0 ? alt : url }], url));
                    }
                    else
                    {
                        if (ChatImages.IsImageLink(url)) images?.Add((url, ""));
                        result.Add(Link(Inlines(link), url));
                    }
                    break;
                case AutolinkInline link:
                    if (!link.IsEmail && ChatImages.IsImageLink(link.Url)) images?.Add((link.Url, ""));
                    result.Add(Link([new CRun { Text = link.Url }], link.IsEmail ? "mailto:" + link.Url : link.Url));
                    break;
                case HtmlEntityInline entity:
                    result.Add(new CRun { Text = entity.Transcoded.ToString() });
                    break;
                case TaskList task:
                    result.Add(new CRun { Text = task.Checked ? "☑" : "☐" });
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested, images));
                    break;
            }
        }
        return result.ToArray();
    }
    private CHyperlink Link(CInline[] children, string url) => new(children)
    {
        CommandParameter = url,
        Command = target => { if (HyperlinkCommand?.CanExecute(target) == true) HyperlinkCommand.Execute(target); }
    };

    // ChatMarkdown fills these placeholders, so loading and theming stay with its resources.
    private sealed class ImageElement(string url, string alt) : DocumentElement
    {
        private readonly Lazy<Control> control = new(() => new Border { Classes = { "ChatImage" }, Tag = new ChatImage(url, alt), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Margin = new Thickness(0, 4) });
        public override Control Control => control.Value;
        public override IEnumerable<DocumentElement> Children => [];
        public override void Select(Point from, Point to) => Helper?.Register(Control);
        public override void UnSelect() => Helper?.Unregister(Control);
        public override void ConstructSelectedText(StringBuilder builder) => builder.Append("![").Append(alt).Append("](").Append(url).Append(')');
    }
    private sealed class MermaidElement(string text) : DocumentElement
    {
        private readonly Lazy<Control> control = new(() => new Border { Classes = { "CodeBlock", "MermaidBlock" }, Tag = text });
        public override Control Control => control.Value;
        public override IEnumerable<DocumentElement> Children => [];
        public override void Select(Point from, Point to) => Helper?.Register(Control);
        public override void UnSelect() => Helper?.Unregister(Control);
        public override void ConstructSelectedText(StringBuilder builder) => builder.Append("```mermaid\n").Append(text).Append("\n```");
    }

    // Code is text in the document like any other, so it selects and copies with the message
    // around it. ChatMarkdown adds the header and copy button, and colours the syntax.
    public sealed class CodeElement : DocumentElement
    {
        private readonly Lazy<CTextBlock> block;
        private readonly Lazy<Control> control;
        public CodeElement(string language, IHighlightingDefinition? syntax, string text)
        {
            Language = language; Syntax = syntax; Text = text;
            block = new(() => new CTextBlock { Classes = { "CodeText" }, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Content = new AvaloniaList<CInline>(Runs(text, null)), Tag = this });
            control = new(() => new Border { Classes = { "CodeBlock" }, Child = block.Value });
        }
        public string Language { get; }
        public IHighlightingDefinition? Syntax { get; }
        public string Text { get; }
        public CTextBlock Block => block.Value;
        public override Control Control => control.Value;
        public override IEnumerable<DocumentElement> Children => [];
        // Points arrive relative to the whole block, header included.
        public override void Select(Point from, Point to)
        {
            var origin = Block.TranslatePoint(default, Control) ?? default;
            Block.Select(Block.CalcuatePointerFrom(from.X - origin.X, from.Y - origin.Y), Block.CalcuatePointerFrom(to.X - origin.X, to.Y - origin.Y));
        }
        public override void UnSelect() => Block.ClearSelection();
        public override void ConstructSelectedText(StringBuilder builder) => builder.Append(Block.GetSelectedText());

        // One run per stretch of same-coloured characters, and a line break between lines.
        public static IEnumerable<CInline> Runs(string text, IHighlightingDefinition? syntax)
        {
            var document = new TextDocument(text);
            using var highlighter = syntax is null ? null : new DocumentHighlighter(document, syntax);
            var runs = new List<CInline>();
            foreach (var line in document.Lines)
            {
                if (line.LineNumber > 1) runs.Add(new CLineBreak());
                var content = document.GetText(line);
                if (content.Length == 0) continue;
                var colors = new HighlightingColor?[content.Length];
                if (highlighter?.HighlightLine(line.LineNumber) is { } highlighted)
                    // Sections are ordered outermost first, so a nested section paints over its parent.
                    foreach (var section in highlighted.Sections)
                        for (var i = Math.Max(0, section.Offset - line.Offset); i < Math.Min(content.Length, section.Offset + section.Length - line.Offset); i++) colors[i] = section.Color;
                for (var start = 0; start < content.Length;)
                {
                    var end = start + 1;
                    while (end < content.Length && colors[end] == colors[start]) end++;
                    var run = new CRun { Text = content[start..end] };
                    if (colors[start] is { } color)
                    {
                        if (color.Foreground?.GetBrush(null) is { } brush) run.Foreground = brush;
                        if (color.FontWeight is { } weight) run.FontWeight = weight;
                        if (color.FontStyle is { } style) run.FontStyle = style;
                    }
                    runs.Add(run); start = end;
                }
            }
            return runs;
        }
    }
}
public sealed record ChatImage(string Url, string Alt);
