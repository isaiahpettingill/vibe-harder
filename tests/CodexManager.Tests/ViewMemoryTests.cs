using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class ViewMemoryTests
{
    [AvaloniaFact]
    public void SleepingRemoteReleasesItsTranscriptAndRestoresThePage()
    {
        using var view = new RemoteView(new RemoteHost("Test", "127.0.0.1", 1, "unused", "unused"));
        view.SetConnectionCollapsed(true);
        var output = (ListBox)typeof(RemoteView).GetField("output", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        var page = new[] { new Message { Text = "Preserve this history page" } };
        output.ItemsSource = page;
        view.SetPresentationSleeping(true); Assert.Null(output.ItemsSource);
        view.SetPresentationSleeping(false); Assert.Same(page, output.ItemsSource);
    }

    [Fact]
    public void RemoteSnapshotsDoNotRetainEvictedMessages()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("snapshot-memory-").FullName);
        using var service = new SessionService(store, [], [], (_, _) => throw new InvalidOperationException());
        var weak = Snapshot(service);
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(service);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Snapshot(SessionService service)
    {
        var message = new Message { Text = new string('x', 100000) };
        message.Attachments.Add(new("large", "text/plain", new string('a', 1000000)));
        typeof(SessionService).GetMethod("MessageRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [message, null]);
        return new(message);
    }

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
