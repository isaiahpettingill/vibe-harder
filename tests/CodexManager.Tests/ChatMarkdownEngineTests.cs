using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;

namespace CodexManager.Tests;

public class ChatMarkdownEngineTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongNonTableLinesDoNotRunTheBacktrackingTableDetector(bool pipe)
    {
        // Synthetic equivalent of the restored long messages that hung the UI.
        // A missing delimiter row forced the old unanchored regex to rescan the
        // entire remaining line at every character.
        var text = new string('x', 250_000) + (pipe ? " | not a table" : "");
        var timer = Stopwatch.StartNew();
        var document = new ChatMarkdownEngine().TransformElement(text);
        var paragraph = Assert.IsType<CTextBlockElement>(Assert.Single(document.Children));
        Assert.Equal(text, Assert.IsType<CTextBlock>(paragraph.Control).Text);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Parsing took {timer.Elapsed}");
    }

    [AvaloniaFact]
    public void TablesComeFromTheAstAndKeepAlignmentEscapesAndInlineFormatting()
    {
        var document = new ChatMarkdownEngine().TransformElement("| Left | Right |\n| :--- | ---: |\n| **bold** | a\\|b |\n");
        var table = Assert.IsType<TableBlockElement>(Assert.Single(document.Children));
        var cells = Descendants(table).OfType<TableCellElement>().ToArray();
        Assert.Equal(4, cells.Length);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, cells[2].Horizontal);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, cells[3].Horizontal);
        var body = Assert.IsType<CTextBlock>(Assert.Single(cells[2].Children).Control);
        Assert.Single(body.Content.OfType<CBold>());
        Assert.Equal("a|b", Assert.IsType<CTextBlock>(Assert.Single(cells[3].Children).Control).Text.Trim());
    }

    [AvaloniaFact]
    public void CommonMarkBlocksAndReferencesKeepTheirDocumentStructure()
    {
        var document = new ChatMarkdownEngine().TransformElement("# Title\n\n> Quote\n\n- one\n  - nested\n- [x] done\n\n[report][r]\n\n[r]: https://example.com\n\n---\n");
        Assert.Contains(document.Children, child => child is HeaderElement);
        Assert.Contains(document.Children, child => child is BlockquoteElement);
        Assert.Equal(2, Descendants(document).OfType<ListBlockElement>().Count());
        var blocks = Descendants(document).OfType<CTextBlockElement>().Select(element => (CTextBlock)element.Control).ToArray();
        Assert.Contains(blocks, block => block.Text.Contains("☑ done"));
        var link = Assert.Single(blocks.SelectMany(block => block.Content).OfType<CHyperlink>());
        Assert.Equal("https://example.com", link.CommandParameter);
        Assert.DoesNotContain(blocks, block => block.Text.Contains("[r]:"));
    }

    [AvaloniaFact]
    public void InlineCodeEmphasisEntitiesAndAutolinksAreNotLost()
    {
        var document = new ChatMarkdownEngine().TransformElement("**bold** *italic* ~~gone~~ `x < y` &amp; <https://example.com>");
        var block = Assert.IsType<CTextBlock>(Assert.Single(document.Children).Control);
        Assert.Contains(block.Content, inline => inline is CBold);
        Assert.Contains(block.Content, inline => inline is CItalic);
        Assert.Contains(block.Content, inline => inline is CStrikethrough);
        Assert.Contains(block.Content, inline => inline is CCode);
        Assert.Contains(block.Content, inline => inline is CHyperlink);
        Assert.Contains("x < y &", block.Text);
    }

    [AvaloniaFact]
    public void UnclosedStreamingFencesKeepTheirExactCodeWithoutParsingIt()
    {
        var source = "```csharp\nvar text = \"| ---- ** not markdown\";";
        var document = new ChatMarkdownEngine().TransformElement(source);
        var element = Assert.Single(document.Children);
        var editor = Assert.Single(element.Control.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
        Assert.Equal("var text = \"| ---- ** not markdown\";", editor.Text);
        Assert.NotNull(editor.SyntaxHighlighting);
    }

    [AvaloniaFact]
    public void RawHtmlIsVisibleTextRatherThanActiveControls()
    {
        const string source = "<script>alert('test')</script>";
        var document = new ChatMarkdownEngine().TransformElement(source);
        Assert.Equal(source, Assert.IsType<CTextBlock>(Assert.Single(document.Children).Control).Text);
    }

    private static IEnumerable<DocumentElement> Descendants(DocumentElement element)
    {
        foreach (var child in element.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
