using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class TranscriptProgressTests
{
    private static ListBox Transcript(IEnumerable<int> messages) => new()
    {
        ItemsSource = messages,
        ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()),
        ItemTemplate = new FuncDataTemplate<int>((n, _) => new TextBlock { Text = "Message " + n, Height = 60 })
    };

    [Trait("Category", "CI")]
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
}
