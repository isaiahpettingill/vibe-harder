using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;

namespace CodexManager.Tests;

public class TouchSelectionTests
{
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("alpha beta", 2, "alpha")]
    [InlineData("alpha beta", 5, "alpha")]
    [InlineData("alpha beta", 6, "beta")]
    [InlineData("alpha beta", 10, "beta")]
    [InlineData("call foo_bar(1)", 7, "foo_bar")]
    [InlineData("a  b", 2, " ")]
    [InlineData("", 0, "")]
    public void HoldingACharacterSelectsItsWord(string text, int index, string word)
    {
        var (from, to) = TouchSelection.Word(text, index);
        Assert.Equal(word, text[from..to]);
    }

    // Holding a word selects it in place; a finger that moves is scrolling, not selecting. The end
    // handle dragged past the line takes in the rest of it, and Copy puts the selection on the clipboard.
    [Trait("Category", "Integration")]
    [AvaloniaTheory]
    [InlineData(false, 0, false, "alpha")]
    [InlineData(true, 0, false, "gamma")]
    [InlineData(false, 0, true, "alpha beta gamma")]
    [InlineData(false, 40, false, "")]
    public async Task HoldingChatTextSelectsAWordWithHandlesToCopy(bool atEnd, double moved, bool dragEnd, string expected)
    {
        var markdown = new ChatMarkdown { Text = "alpha beta gamma" };
        var window = new Window { Content = markdown, Width = 600, Height = 300 }; window.Show();
        try
        {
            CTextBlock? Block() => markdown.GetVisualDescendants().OfType<CTextBlock>().FirstOrDefault();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Block() is null && DateTime.UtcNow < until) { window.UpdateLayout(); await Task.Delay(25); }
            var block = Block()!;
            await ComposerInputTests.Touch(block, window, 650, moved, new Point(atEnd ? block.Bounds.Width - 2 : 2, block.Bounds.Height / 2));
            window.UpdateLayout();
            if (dragEnd)
            {
                var handle = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SelectionEndHandle");
                using var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
                var from = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                var to = new Point(block.TranslatePoint(new Point(block.Bounds.Width + 40, 0), window)!.Value.X, from.Y);
                handle.RaiseEvent(new PointerPressedEventArgs(handle, finger, window, from, 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                handle.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, handle, finger, window, to, 2, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                handle.RaiseEvent(new PointerReleasedEventArgs(handle, finger, window, to, 3, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            }
            Assert.Equal(expected, markdown.TouchSelection.SelectedText);
            Assert.Equal(expected.Length > 0, markdown.TouchSelection.IsActive);
            if (expected.Length == 0) return;
            window.UpdateLayout();
            var copy = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopySelection");
            Assert.True(copy.IsEffectivelyVisible);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            async Task<string?> Copied() { using var data = await window.Clipboard!.TryGetDataAsync(); return data is null ? null : await data.TryGetTextAsync(); }
            until = DateTime.UtcNow.AddSeconds(5);
            while (await Copied() != expected && DateTime.UtcNow < until) await Task.Delay(25);
            Assert.Equal(expected, await Copied());
            Assert.False(markdown.TouchSelection.IsActive);
        }
        finally { window.Close(); }
    }

    // In the chat itself, holding a word in a reply selects it there, inside the scrolling transcript.
    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task HoldingAWordInAReplySelectsItInTheTranscript()
    {
        var directory = Directory.CreateTempSubdirectory("touch-select-").FullName;
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Select", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        store.SaveMessage(chat, new Message { Role = "user", Text = "question" });
        store.SaveMessage(chat, new Message { Role = "assistant", Text = "Keepsake answer text" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var view = new MainView(store, [workspace], [chat]);
        var window = new Window { Content = view, Width = 900, Height = 700 }; window.Show();
        try
        {
            ChatMarkdown? Reply() => view.GetVisualDescendants().OfType<ChatMarkdown>().FirstOrDefault(m => m.Text == "Keepsake answer text" && m.GetVisualDescendants().OfType<CTextBlock>().Any());
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Reply() is null && DateTime.UtcNow < until) { window.UpdateLayout(); await Task.Delay(25); }
            var reply = Reply()!; var block = reply.GetVisualDescendants().OfType<CTextBlock>().First();
            await ComposerInputTests.Touch(block, window, 650, at: new Point(2, block.Bounds.Height / 2));
            Assert.Equal("Keepsake", reply.TouchSelection.SelectedText);
        }
        finally { window.Close(); }
    }
}
