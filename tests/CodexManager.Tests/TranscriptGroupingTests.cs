using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class TranscriptGroupingTests
{
    [AvaloniaFact]
    public void GroupsAreLazyNestedExpandableAndSearchRevealsTheirMembers()
    {
        var messages = new ObservableCollection<Message>
        {
            new() { Role = "user", Text = "Request" },
            new() { Role = "thought", Text = "Reasoning" },
            new() { Role = "tool", Text = "A tool result" },
            new() { Role = "thought", Text = "More reasoning" },
            new() { Role = "assistant", Text = "Answer" }
        };
        var list = new ListBox { ItemsSource = messages, ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()), ItemTemplate = new FuncDataTemplate<Message>((m, _) => new MessageView { Message = m }) };
        TranscriptPanel.SetShowProgress(list, true);
        var window = new Window { Content = list, Width = 600, Height = 650 }; window.Show();
        try
        {
            window.UpdateLayout();
            var group = Assert.Single(list.GetVisualDescendants().OfType<TranscriptActionGroup>());
            Assert.Empty(group.GetVisualDescendants().OfType<MessageView>());
            group.Expand(); window.UpdateLayout();
            Assert.Equal(3, group.GetVisualDescendants().OfType<MessageView>().Count());
            Assert.All(group.GetVisualDescendants().OfType<MessageView>(), v => Assert.False(v.IsExpandedOutput));
            Assert.Empty(group.GetVisualDescendants().OfType<ChatMarkdown>());
            var panel = (TranscriptPanel)list.ItemsPanelRoot!;
            panel.RevealMessage(2); window.UpdateLayout();
            group = Assert.Single(list.GetVisualDescendants().OfType<TranscriptActionGroup>());
            Assert.True(group.GetVisualDescendants().OfType<MessageView>().Single(v => v.Message == messages[2]).IsExpandedOutput);
            Assert.Single(group.GetVisualDescendants().OfType<ChatMarkdown>());
            group.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToggleActionGroup").RaiseEvent(new(Button.ClickEvent));
            window.UpdateLayout(); Assert.Empty(group.GetVisualDescendants().OfType<MessageView>());
            var progress = panel.Children.OfType<ChatProgressIndicator>().Single();
            Assert.Equal(list.GetVisualDescendants().OfType<ListBoxItem>().Max(c => c.Bounds.Bottom), progress.Bounds.Top, 2);
            Assert.Equal(5, list.ItemCount);
        }
        finally { window.Close(); }
    }
}
