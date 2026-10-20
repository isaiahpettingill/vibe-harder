using Avalonia.Headless.XUnit;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;

namespace CodexManager.Tests;

public class ChatMediaTests
{
    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void MermaidFencesAndImageLinksBecomeTheirOwnBlocks()
    {
        var engine = new ChatMarkdownEngine();
        var diagram = Assert.Single(engine.TransformElement("```mermaid\ngraph TD\n  A --> B\n```").Children).Control;
        Assert.Contains("MermaidBlock", diagram.Classes); Assert.Equal("graph TD\n  A --> B", diagram.Tag);
        // While the fence is still streaming the diagram is incomplete, so show its source.
        var streaming = Assert.Single(engine.TransformElement("```mermaid\ngraph TD\n  A -->").Children).Control;
        Assert.Contains("CodeBlock", streaming.Classes); Assert.DoesNotContain("MermaidBlock", streaming.Classes);

        var only = Assert.Single(engine.TransformElement("![The chart](https://example.com/chart.png)").Children).Control;
        Assert.Equal(new ChatImage("https://example.com/chart.png", "The chart"), only.Tag);
        var linked = engine.TransformElement("See [the chart](https://example.com/a/chart.PNG?x=1) and https://example.com/b.webp or [docs](https://example.com/docs).").Children.ToArray();
        var text = Assert.IsType<CTextBlock>(linked[0].Control);
        Assert.Equal(3, text.Content.OfType<CHyperlink>().Count());
        Assert.Equal(["https://example.com/a/chart.PNG?x=1", "https://example.com/b.webp"], linked.Skip(1).Select(e => ((ChatImage)e.Control.Tag!).Url));
        var inline = engine.TransformElement("Before ![logo](logo.png) after").Children.ToArray();
        Assert.Contains("logo", Assert.IsType<CTextBlock>(inline[0].Control).Text);
        Assert.Equal("logo.png", ((ChatImage)inline[1].Control.Tag!).Url);
        Assert.False(ChatImages.IsImageLink("https://example.com/chart.png.html"));
        Assert.True(ChatImages.IsImageLink("data:image/png;base64,AAAA"));
    }

}
