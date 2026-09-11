using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class ViewMemoryTests
{
    [AvaloniaFact]
    public async Task CollapsedOutputReleasesRendererAndClosedViewIsCollectible()
    {
        var message = new Message { Role = "tool", Text = "```sh\n" + new string('x', 10000) + "\n```" };
        var weak = Exercise(message);
        await Task.Delay(150); // Drain renderer decoration callbacks.
        for (var i = 0; i < 3 && weak.IsAlive; i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(30); }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(message); // A surviving model must not retain the detached view.
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Exercise(Message message)
    {
        var view = new MessageView { Message = message };
        var window = new Window { Content = view }; window.Show();
        Assert.Empty(view.GetVisualDescendants().OfType<ChatMarkdown>());
        view.GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.Single(view.GetVisualDescendants().OfType<ChatMarkdown>());
        view.Collapse(); window.UpdateLayout(); Assert.Empty(view.GetVisualDescendants().OfType<ChatMarkdown>());
        window.Content = null; window.Close();
        return new(view);
    }
    [Fact]
    public async Task ReleasedHistoryReloadsWithoutLosingSequenceOrText()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "vibe-memory", Guid.NewGuid().ToString("N")));
        store.Save(new Workspace("w", "Test", "."));
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        chat.Messages.Add(new Message { Text = "Saved output" });
        store.ReleaseHistory(chat);
        Assert.Empty(chat.Messages); Assert.False(chat.HistoryLoaded); Assert.Equal(1, chat.NextSequence);
        store.ApplyRecentPage(chat, await store.ReadPageAsync(chat));
        Assert.Equal("Saved output", Assert.Single(chat.Messages).Text);
        chat.Busy = true; store.ReleaseHistory(chat); Assert.Single(chat.Messages);
    }
}
