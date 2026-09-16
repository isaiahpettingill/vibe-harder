using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class TranscriptProgressTests
{
    private static ListBox Transcript(IEnumerable<int> messages) => new()
    {
        ItemsSource = messages,
        ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()),
        ItemTemplate = new FuncDataTemplate<int>((n, _) => new TextBlock { Text = "Message " + n, Height = 60 })
    };

    [AvaloniaFact]
    public void LoaderFollowsShortTranscriptAndMovesWithNewMessages()
    {
        var messages = new ObservableCollection<int> { 1 };
        var list = Transcript(messages); TranscriptPanel.SetShowProgress(list, true);
        var window = new Window { Content = list, Width = 400, Height = 500 }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            var progress = panel.Children.OfType<ChatProgressIndicator>().Single();
            var last = list.GetVisualDescendants().OfType<ListBoxItem>().Last();
            Assert.True(progress.IsVisible);
            Assert.Equal(last.Bounds.Bottom, progress.Bounds.Top, 2);
            Assert.True(progress.Bounds.Bottom < panel.Bounds.Height / 2);
            var previousTop = progress.Bounds.Top;
            messages.Add(2); window.UpdateLayout();
            last = list.GetVisualDescendants().OfType<ListBoxItem>().Last();
            Assert.Equal(last.Bounds.Bottom, progress.Bounds.Top, 2);
            Assert.True(progress.Bounds.Top > previousTop);
            Assert.Equal(2, list.ItemCount); // The footer never becomes a saved message.
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LoaderScrollsAwayAndLeavesNoSpaceWhenFinished()
    {
        var animations = VisibleAnimation.ActiveCount;
        var list = Transcript(Enumerable.Range(0, 100)); TranscriptPanel.SetShowProgress(list, true);
        var window = new Window { Content = list, Width = 400, Height = 300 }; window.Show();
        try
        {
            list.ScrollIntoView(99); window.UpdateLayout(); window.UpdateLayout();
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            var progress = panel.Children.OfType<ChatProgressIndicator>().Single();
            Assert.InRange(progress.Bounds.Bottom, 1, panel.Bounds.Height);
            Assert.Equal(animations + 1, VisibleAnimation.ActiveCount);
            panel.Offset = new Vector(0, 0); window.UpdateLayout(); window.UpdateLayout();
            Assert.True(progress.Bounds.Top > panel.Bounds.Height);
            Assert.Equal(animations, VisibleAnimation.ActiveCount);
            var extent = panel.Extent.Height;
            TranscriptPanel.SetShowProgress(list, false); window.UpdateLayout();
            Assert.False(progress.IsVisible);
            Assert.Equal(extent - progress.Height, panel.Extent.Height, 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyTranscriptShowsLoaderAtTheStart()
    {
        var list = Transcript([]); TranscriptPanel.SetShowProgress(list, true);
        var window = new Window { Content = list, Width = 400, Height = 300 }; window.Show();
        try
        {
            window.UpdateLayout();
            var progress = list.GetVisualDescendants().OfType<ChatProgressIndicator>().Single();
            Assert.True(progress.IsVisible); Assert.Equal(0, progress.Bounds.Top);
        }
        finally { window.Close(); }
    }
}
