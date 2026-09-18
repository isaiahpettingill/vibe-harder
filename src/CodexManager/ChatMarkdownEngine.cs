using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
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
                    yield return new CTextBlockElement(Inlines(paragraph.Inline), "Paragraph");
                    break;
                case CodeBlock code:
                    var language = (code as FencedCodeBlock)?.Info ?? "";
                    var text = code.Lines.ToString();
                    yield return string.IsNullOrWhiteSpace(language) ? new PlainCodeBlockElement(text) : new CodeElement(language, text);
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
    private CInline[] Inlines(ContainerInline? container)
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
                    var children = Inlines(emphasis);
                    result.Add(emphasis.DelimiterChar == '~' ? new CStrikethrough(children)
                        : emphasis.DelimiterCount == 2 ? new CBold(children) : new CItalic(children));
                    break;
                case LinkInline link:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    if (link.IsImage)
                    {
                        // Retain the existing asynchronous image loader, not its parser.
                        result.Add(Plugins.Info.LoadImage(url));
                    }
                    else result.Add(Link(Inlines(link), url));
                    break;
                case AutolinkInline link:
                    result.Add(Link([new CRun { Text = link.Url }], link.IsEmail ? "mailto:" + link.Url : link.Url));
                    break;
                case HtmlEntityInline entity:
                    result.Add(new CRun { Text = entity.Transcoded.ToString() });
                    break;
                case TaskList task:
                    result.Add(new CRun { Text = task.Checked ? "☑" : "☐" });
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested));
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

    private sealed class CodeElement : DocumentElement
    {
        private readonly string text;
        private readonly Lazy<Control> control;
        public CodeElement(string language, string text)
        {
            this.text = text;
            control = new(() => new Border
            {
                Classes = { "CodeBlock" },
                Child = new StackPanel { Children = { new TextEditor { Text = text, Tag = language, IsReadOnly = true, SyntaxHighlighting = Highlighting.Solve(language) } } }
            });
        }
        public override Control Control => control.Value;
        public override IEnumerable<DocumentElement> Children => [];
        public override void Select(Point from, Point to) => Helper?.Register(Control);
        public override void UnSelect() => Helper?.Unregister(Control);
        public override void ConstructSelectedText(StringBuilder builder) => builder.Append(text);
    }
}
