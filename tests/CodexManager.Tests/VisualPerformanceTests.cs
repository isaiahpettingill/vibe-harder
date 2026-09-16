using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class VisualPerformanceTests
{
    [AvaloniaFact]
    public void TranscriptUsesItsScrollPresenterClip()
    {
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 100).ToArray(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new TranscriptPanel()),
            ItemTemplate = new FuncDataTemplate<int>((number, _) => new TextBlock { Text = number.ToString(), Height = 100 })
        };
        var window = new Window { Width = 400, Height = 300, Content = list }; window.Show();
        try
        {
            window.UpdateLayout();
            var panel = list.GetVisualDescendants().OfType<TranscriptPanel>().Single();
            Assert.False(panel.ClipToBounds);
            Assert.True(panel.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().Single().ClipToBounds);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RemoteSidebarKeepsControlsAndModelsAcrossStatusChanges()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("sidebar-cache-").FullName);
        store.Setting("remoteEnabled", "0");
        var main = new MainView(store, remoteOnly: true);
        var host = new RemoteHost("Test", "127.0.0.1", 1, "unused", "unused");
        using var remote = new RemoteView(host); remote.SetConnectionCollapsed(true);
        var section = new StackPanel();
        T Field<T>(string name) => (T)typeof(MainView).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Field<Dictionary<RemoteHost, RemoteView>>("remoteViews").Add(host, remote);
        Field<Dictionary<string, StackPanel>>("remoteSections").Add("127.0.0.1:1", section);
        var catalog = System.Text.Json.Nodes.JsonNode.Parse("""
            {"workspaces":[{"id":"w","name":"Workspace"}],"chats":[{"id":"c","workspaceId":"w","title":"Chat","status":"Working","busy":true,"archived":false,"provider":"Codex"}]}
            """)!;
        Field<Dictionary<RemoteHost, System.Text.Json.Nodes.JsonNode>>("remoteCatalogs").Add(host, catalog);
        void Refresh() => typeof(MainView).GetMethod("RefreshRemoteSidebar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
        try
        {
            Refresh(); var group = (StackPanel)section.Children[1]; var list = (ListBox)group.Children[1];
            var items = list.ItemsSource; var chat = (Chat)list.Items[0]!;
            catalog["chats"]![0]!["status"] = "Ready"; catalog["chats"]![0]!["busy"] = false;
            Refresh();
            Assert.Same(group, section.Children[1]); Assert.Same(items, list.ItemsSource);
            Assert.Same(chat, list.Items[0]); Assert.False(chat.Busy); Assert.Equal("Ready", chat.Status);
            catalog["chats"]!.AsArray().Clear(); Refresh();
            Assert.Same(group, section.Children[1]); Assert.Empty(list.Items);
            Assert.Empty(Field<Dictionary<string, Chat>>("remoteActivity"));
            catalog["workspaces"]!.AsArray().Clear(); Refresh(); Assert.Single(section.Children);
        }
        finally { main.DisposeMobile(); }
    }

    [AvaloniaFact]
    public void WorkspaceTintIsOpaqueAndUpdatesWithTheTheme()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("opaque-tint-").FullName);
        var previous = AppTheme.Current ?? AppTheme.All[0];
        try
        {
            store.Setting("workspaceColor:w", "#89B4FA");
            AppTheme.Apply(AppTheme.All.First(t => !t.Light));
            var brush = Assert.IsType<Avalonia.Media.SolidColorBrush>(SidebarColors.Brush(store, "workspaceColor:w", true));
            var dark = brush.Color; Assert.Equal(255, dark.A); Assert.Equal(1, brush.Opacity);
            AppTheme.Apply(AppTheme.All.First(t => t.Light));
            Assert.Same(brush, SidebarColors.Brush(store, "workspaceColor:w", true));
            Assert.NotEqual(dark, brush.Color); Assert.Equal(255, brush.Color.A);
        }
        finally { AppTheme.Apply(previous); }
    }

    [AvaloniaFact]
    public void AnimationsStopWhenAncestorIsHiddenAndOnDetach()
    {
        var initial = VisibleAnimation.ActiveCount;
        var parent = new StackPanel { Children = { new LoadingSpinner(), new ConnectingIndicator(), new ChatProgressIndicator { IsVisible = true } } };
        var window = new Window { Width = 400, Height = 200, Content = parent }; window.Show();
        try
        {
            window.UpdateLayout(); Assert.Equal(initial + 3, VisibleAnimation.ActiveCount);
            parent.IsVisible = false; window.UpdateLayout(); Assert.Equal(initial, VisibleAnimation.ActiveCount);
            parent.IsVisible = true; window.UpdateLayout(); Assert.Equal(initial + 3, VisibleAnimation.ActiveCount);
            window.Content = null; Assert.Equal(initial, VisibleAnimation.ActiveCount);
        }
        finally { window.Close(); }
        Assert.Equal(initial, VisibleAnimation.ActiveCount);
    }

    [AvaloniaFact]
    public async Task MarkdownCoalescesStreamingAndRendersLatestTextAfterReattachment()
    {
        var markdown = new ChatMarkdown { Text = "Initial" };
        var window = new Window { Width = 400, Height = 200, Content = markdown }; window.Show();
        try
        {
            Assert.Equal("Initial", markdown.Markdown);
            for (var i = 0; i < 100; i++) markdown.Text = "Update " + i;
            Assert.Equal("Initial", markdown.Markdown);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal("Update 99", markdown.Markdown);
            window.Content = null; markdown.Text = "Detached update";
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal("Update 99", markdown.Markdown);
            window.Content = markdown; window.UpdateLayout(); Assert.Equal("Detached update", markdown.Markdown);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarVirtualizesInsideItsOuterScroller()
    {
        var list = new SidebarChatList
        {
            ItemsSource = Enumerable.Range(0, 1000).ToArray(),
            ItemTemplate = new FuncDataTemplate<int>((number, _) => new TextBlock { Text = number.ToString(), Height = 40 })
        };
        var scroller = new ScrollViewer { Content = new StackPanel { Children = { new TextBlock { Text = "Workspace" }, list } } };
        var window = new Window { Width = 300, Height = 300, Content = scroller }; window.Show();
        try
        {
            window.UpdateLayout();
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 30);
            scroller.Offset = new Vector(0, 20000); window.UpdateLayout(); window.UpdateLayout();
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 30);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "0");
            list.ScrollIntoView(999); window.UpdateLayout(); window.UpdateLayout();
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "999");
        }
        finally { window.Close(); }
    }
}
