using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;
using LiveMarkdown.Avalonia;

namespace CodexManager.Tests;

public class ChatMediaTests
{
    private static async Task Wait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }
    private static byte[] Png(int width, int height)
    {
        using var image = new RenderTargetBitmap(new PixelSize(width, height));
        using (var drawing = image.CreateDrawingContext()) drawing.FillRectangle(Avalonia.Media.Brushes.Coral, new Rect(0, 0, width, height));
        using var bytes = new MemoryStream(); image.Save(bytes, PngBitmapEncoderOptions.Default); return bytes.ToArray();
    }
    // Serves one PNG over plain HTTP, like an image host a model links to.
    private static (string Url, Task Served) ServeImage(byte[] png)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/chart.png?size=large";
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); listener.Stop();
            await using var stream = client.GetStream();
            var buffer = new byte[4096]; var request = "";
            while (!request.Contains("\r\n\r\n")) request += System.Text.Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(png);
        });
        return (url, served);
    }

    [AvaloniaFact]
    public void MermaidFencesAndImageLinksBecomeTheirOwnBlocks()
    {
        var engine = new ChatMarkdownEngine();
        var diagram = Assert.Single(engine.TransformElement("```mermaid\ngraph TD\n  A --> B\n```").Children).Control;
        Assert.Contains("MermaidBlock", diagram.Classes); Assert.Equal("graph TD\n  A --> B", diagram.Tag);
        // While the fence is still streaming the diagram is incomplete, so show its source.
        var streaming = Assert.Single(engine.TransformElement("```mermaid\ngraph TD\n  A -->").Children).Control;
        Assert.Single(streaming.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());

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

    [AvaloniaFact]
    public async Task RepliesRenderDiagramsAndWebWorkspaceAndEmbeddedImages()
    {
        var directory = Directory.CreateTempSubdirectory("chat-media-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        File.WriteAllBytes(Path.Combine(directory, "screenshot.png"), Png(3000, 600));
        var (url, served) = ServeImage(Png(40, 30));
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Media", directory)); var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        store.SaveMessage(chat, new Message { Role = "assistant", Text = $$"""
            Here is the flow:

            ```mermaid
            graph TD
              A[Start] --> B{Decision}
              B -->|Yes| C[Ship it]
              B -->|No| D[Fix it]
            ```

            The screenshot ![screenshot](screenshot.png), the hosted chart {{url}} and an embedded swatch:

            ![swatch](data:image/png;base64,{{Convert.ToBase64String(Png(12, 12))}})

            ```mermaid
            graph TD
              A --> [broken
            ```
            """ });
        var window = new MainWindow(store) { Width = 1100, Height = 1400 }; window.Show();
        try
        {
            Image[] Images() => window.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is ChatImage).Select(b => b.Child).OfType<Image>().ToArray();
            await Wait(() => { window.UpdateLayout(); return Images().Length == 3; });
            await served;
            var images = Images();
            Assert.Equal(1600, ((Bitmap)images[0].Source!).PixelSize.Width);
            Assert.True(images[0].Bounds.Width < 1100, "Wide screenshots shrink to the transcript.");
            Assert.Equal(new Size(40, 30), images[1].Bounds.Size);
            var diagrams = window.GetVisualDescendants().OfType<MermaidPresenter>().ToArray();
            Assert.Equal(2, diagrams.Length);
            Assert.All(diagrams, d => Assert.True(d.Bounds.Width > 40 && d.Bounds.Height > 40));
            Assert.Equal("graph TD\n  A[Start] --> B{Decision}\n  B -->|Yes| C[Ship it]\n  B -->|No| D[Fix it]", diagrams[0].Text!.ReplaceLineEndings("\n"));
            var block = diagrams[0].GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("MermaidBlock"));
            var source = block.GetVisualDescendants().OfType<IconButton>().First(b => b.Name == "MermaidSource");
            source.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.False(diagrams[0].IsEffectivelyVisible);
            Assert.Contains(block.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == diagrams[0].Text);
            source.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.True(diagrams[0].IsEffectivelyVisible);
            var markdown = diagrams[0].GetVisualAncestors().OfType<ChatMarkdown>().First();
            var html = markdown.ExportHtml();
            Assert.Contains("language-mermaid", html); Assert.Contains("<img src=\"" + WebUtility.HtmlEncode(url) + "\"", html);
            await Task.Delay(100); window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-chat-media.png")), PngBitmapEncoderOptions.Default);
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }
}
