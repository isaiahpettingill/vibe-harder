using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ColorTextBlock.Avalonia;
using System.Text;

namespace CodexManager.Tests;

public class ChatPresentationTests
{
    [AvaloniaFact]
    public async Task PlainCodeBlocksWrapWithoutCoveringTextAndKeepCodeCopy()
    {
        var code = "adb shell " + string.Join(" ", Enumerable.Repeat("long-command-argument", 30));
        var view = new ChatMarkdown { Text = "completed\n\n```\n" + code + "\n```", Muted = true };
        var window = new Window { Content = view, Width = 360, Height = 600 }; window.Show();
        try
        {
            await Task.Delay(150); window.UpdateLayout();
            var block = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains(code) == true);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, block.TextWrapping);
            Assert.True(block.Bounds.Height > 40);
            Assert.True(block.Bounds.Bottom <= ((Control)block.Parent!).Bounds.Height);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<ScrollViewer>(), s => s.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            var copy = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "CopyCode");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
            using var data = await window.Clipboard!.TryGetDataAsync(); Assert.Equal(code, (await data!.TryGetTextAsync())!.Trim());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HighlightingCanBeDisabledAndRestoredOnVisibleCode()
    {
        var view = new ChatMarkdown { Text = "```csharp\nvar value = 123;\n```" };
        var window = new Window { Content = view }; window.Show();
        try
        {
            await Task.Delay(150); window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.NotNull(editor.SyntaxHighlighting);
            AppTheme.SetSyntaxHighlighting(false);
            Assert.Null(editor.SyntaxHighlighting);
            Assert.Equal("var value = 123;", editor.Text.Trim());
            AppTheme.SetSyntaxHighlighting(true);
            Assert.NotNull(editor.SyntaxHighlighting);
        }
        finally { AppTheme.SetSyntaxHighlighting(true); window.Close(); }
    }
    [AvaloniaFact]
    public async Task StyledSelectionAndCodeCopyKeepTextAndCodeFits()
    {
        var view = new ChatMarkdown { Text = "**bold** and *italic*\n\n```sh\ncargo run --release\n```" };
        var window = new Window { Content = view, Width = 500, Height = 300 }; window.Show(); await Task.Delay(150);
        try
        {
            var block = view.GetVisualDescendants().OfType<CTextBlock>().Single(b => b.Text.Contains("bold"));
            block.Select(0, 4);
            await view.Copy(true);
            using (var data = await window.Clipboard!.TryGetDataAsync())
            {
                Assert.Equal("bold", (await data!.TryGetTextAsync())!.Trim());
                var format = DataFormat.CreateBytesPlatformFormat(OperatingSystem.IsWindows() ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html");
                Assert.Contains("<strong>", Encoding.UTF8.GetString((await data!.TryGetValueAsync(format))!));
            }
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.True(editor.Bounds.Bottom <= ((Control)editor.Parent!).Bounds.Height, $"{editor.Parent!.GetType().Name}: editor {editor.Bounds}, parent {((Control)editor.Parent).Bounds}");
            var copy = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyCode");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
            using var copied = await window.Clipboard!.TryGetDataAsync(); Assert.Contains("cargo run --release", await copied!.TryGetTextAsync());
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void ToolAndThinkingStartCollapsedAndCanCollapseAfterExpansion()
    {
        foreach (var role in new[] { "tool", "thought" })
        {
            var view = new MessageView { Message = new Message { Role = role, Text = "command " + new string('x', 3000) } };
            var window = new Window { Content = view }; window.Show();
            Assert.False(view.IsExpandedOutput);
            view.GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(view.IsExpandedOutput);
            view.Collapse(); Assert.False(view.IsExpandedOutput); window.Close();
        }
    }
}
