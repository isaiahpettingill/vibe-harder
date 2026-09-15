using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;

namespace CodexManager.Tests;

public class SidebarCustomizationTests
{
    [AvaloniaTheory]
    [InlineData(PointerType.Touch)]
    [InlineData(PointerType.Mouse)]
    public async Task LongPressReordersWithoutAGrabHandle(PointerType pointerType)
    {
        var store = new Store(Directory.CreateTempSubdirectory("sidebar-touch-").FullName);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        SidebarOrder.Apply(store, "test", new[] { "first", "second" }, s => s);
        var view = new MainView(store, remoteOnly: true);
        var first = new Grid { Height = 60, Background = Brushes.Transparent };
        var title = new Button { Content = "Workspace", HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var clicked = 0; title.Click += (_, _) => clicked++; first.Children.Add(title);
        var second = new Grid { Height = 60, Background = Brushes.Transparent };
        var register = typeof(MainView).GetMethod("EnableHoldReorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        register.Invoke(view, [first, "test", "first", (Action)(() => { }), null]);
        register.Invoke(view, [second, "test", "second", (Action)(() => { }), null]);
        view.Content = new StackPanel { Children = { first, second } };
        var window = new Window { Width = 300, Height = 250, Content = view }; window.Show();
        try
        {
            var pointer = new Pointer(123, pointerType, true);
            var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var start = first.TranslatePoint(new Point(20, 20), window)!.Value;
            if (pointerType == PointerType.Mouse) window.MouseDown(start, MouseButton.Left);
            else first.RaiseEvent(new PointerPressedEventArgs(first, pointer, window, start, 0, properties, KeyModifiers.None));
            await Task.Delay(600, TestContext.Current.CancellationToken);
            Assert.Equal(.65, first.Opacity);
            var end = second.TranslatePoint(new Point(20, 50), window)!.Value;
            if (pointerType == PointerType.Mouse) { window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); }
            else first.RaiseEvent(new PointerReleasedEventArgs(first, pointer, window, end, 600, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Assert.Equal(0, clicked);
            Assert.Equal(new[] { "second", "first" }, SidebarOrder.Apply(store, "test", new[] { "first", "second" }, s => s));
            Assert.Equal(1, first.Opacity);
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
    [AvaloniaFact]
    public async Task RealChatRowsAllowImmediateMouseDragAndOrdinaryClicksDoNotLatch()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("sidebar-real-drag-").FullName);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var owner = new Workspace("w", "Workspace", store.DirectoryPath); store.Save(owner);
        var chats = Enumerable.Range(0, 3).Select(i => new Chat { Id = "chat" + i, Title = "Chat " + i, WorkspaceId = owner.Id }).ToList();
        foreach (var chat in chats) store.Save(chat);
        var view = new MainView(store, [owner], chats);
        var window = new Window { Content = view, Width = 1000, Height = 700 }; window.Show();
        try
        {
            window.UpdateLayout();
            Control Row(string id) => view.GetVisualDescendants().OfType<ChatActivityIndicator>().Single(c => c.Name == "Activity_" + id).GetVisualAncestors().OfType<Grid>().First(c => c.Classes.Contains("chatRow"));
            var first = Row("chat0");
            var start = first.TranslatePoint(new Point(40, 10), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left);
            await Task.Delay(600, TestContext.Current.CancellationToken);
            Assert.Equal(1, first.Opacity);
            first = Row("chat0"); var second = Row("chat1");
            start = first.TranslatePoint(new Point(40, 10), window)!.Value;
            var end = second.TranslatePoint(new Point(40, second.Bounds.Height - 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left);
            Assert.Equal(new[] { "chat1", "chat0", "chat2" }, SidebarOrder.Apply(store, "chats:w", chats, c => c.Id).Select(c => c.Id));
            window.UpdateLayout();
            first = Row("chat0"); second = Row("chat1");
            start = first.TranslatePoint(new Point(40, 10), window)!.Value;
            end = second.TranslatePoint(new Point(40, 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.MouseUp(end, MouseButton.Left);
            Assert.Equal(new[] { "chat1", "chat0", "chat2" }, SidebarOrder.Apply(store, "chats:w", chats, c => c.Id).Select(c => c.Id));
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
    [AvaloniaFact]
    public void WorkspaceDropUsesHeadingRatherThanExpandedGroupMidpoint()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("sidebar-heading-drop-").FullName);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var view = new MainView(store, remoteOnly: true);
        SidebarOrder.Apply(store, "test", new[] { "first", "second" }, id => id);
        var firstHeading = new Border { Height = 40, Background = Brushes.Transparent };
        var secondHeading = new Border { Height = 40, Background = Brushes.Transparent };
        var first = new StackPanel { Children = { firstHeading, new Border { Height = 60 } } };
        var second = new StackPanel { Children = { secondHeading, new Border { Height = 200 } } };
        var register = typeof(MainView).GetMethod("EnableHoldReorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        register.Invoke(view, [first, "test", "first", (Action)(() => { }), firstHeading]);
        register.Invoke(view, [second, "test", "second", (Action)(() => { }), secondHeading]);
        view.Content = new StackPanel { Children = { first, second } };
        var window = new Window { Content = view, Width = 400, Height = 500 }; window.Show();
        try
        {
            var start = firstHeading.TranslatePoint(new Point(20, 20), window)!.Value;
            var end = secondHeading.TranslatePoint(new Point(20, 35), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left);
            Assert.Equal(new[] { "second", "first" }, SidebarOrder.Apply(store, "test", new[] { "first", "second" }, id => id));
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
    [AvaloniaFact]
    public void WorkspaceActionsRevealOnHoverAndMobileHidesHandles()
    {
        var grip = new IconButton { Classes = { "rowAction", "dragHandle" } };
        var action = new IconButton { Classes = { "rowAction" }, Margin = new Thickness(40, 0, 0, 0) };
        var heading = new Grid { Classes = { "workspaceHeading" }, Background = Brushes.Transparent, Children = { grip, action }, Height = 50 };
        var root = new UserControl { Content = heading };
        var window = new Window { Width = 300, Height = 150, Content = root }; window.Show();
        try
        {
            window.MouseMove(new Point(290, 145)); Assert.Equal(0, grip.Opacity); Assert.Equal(0, action.Opacity);
            window.MouseMove(heading.TranslatePoint(new Point(10, 10), window)!.Value);
            Assert.Equal(1, grip.Opacity); Assert.Equal(1, action.Opacity);
            root.Classes.Add("touchSidebar"); Assert.False(grip.IsVisible); Assert.Equal(.3, action.Opacity);
        }
        finally { window.Close(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceAndChatOrderSurviveActivityAndRestart(bool background)
    {
        var directory = Directory.CreateTempSubdirectory("sidebar-order-").FullName;
        using (var store = new Store(directory, background))
        {
            store.Save(new Workspace("z", "Zebra", directory)); store.Save(new Workspace("a", "Alpha", directory));
            await store.FlushAsync();
            Assert.Equal(new[] { "z", "a" }, store.Workspaces().Select(w => w.Id));
            var first = new Chat { Id = "first", WorkspaceId = "z", Title = "Zebra chat" };
            var second = new Chat { Id = "second", WorkspaceId = "z", Title = "Alpha chat" };
            store.Save(first); store.Save(second);
            SidebarOrder.Apply(store, "chats:z", new[] { first, second }, c => c.Id);
            SidebarOrder.Move(store, "chats:z", "second", "first", false);
            first.Updated = DateTimeOffset.UtcNow.AddDays(1); store.Save(first);
            Assert.Equal(new[] { "second", "first" }, SidebarOrder.Apply(store, "chats:z", new[] { first, second }, c => c.Id).Select(c => c.Id));
            SidebarOrder.Move(store, "workspaces", "a", "z", false);
            store.Setting("workspaceColor:z", "#89B4FA"); store.Setting("chatColor:first", "#F38BA8");
            await store.FlushAsync();
        }
        using var reopened = new Store(directory);
        Assert.Equal(new[] { "a", "z" }, reopened.Workspaces().Select(w => w.Id));
        Assert.Equal(new[] { "second", "first" }, SidebarOrder.Apply(reopened, "chats:z", reopened.Chats(), c => c.Id).Select(c => c.Id));
        Assert.Equal("#89B4FA", reopened.Setting("workspaceColor:z")); Assert.Equal("#F38BA8", reopened.Setting("chatColor:first"));
    }
    [AvaloniaFact]
    public void RequestedThemesAndModelIconsRenderWithAccentContrast()
    {
        var original = AppTheme.Current ?? AppTheme.All[0];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(20) };
        foreach (var name in new[] { "openai", "claude", "agent", "steer", "stop" }) row.Children.Add(new IconButton { Icon = name, Classes = { "accent" } });
        var window = new Window { Content = row, Width = 360, Height = 90 }; window.Show();
        try
        {
            foreach (var name in new[] { "Quiet Light", "Tokyo Night", "Tokyo Night Storm", "Tokyo Night Light", "Monokai Dimmed", "Nord Frost", "Nord Aurora", "Catppuccin Latte", "Catppuccin Mocha" })
            {
                var theme = AppTheme.All.Single(p => p.Name == name); Assert.Equal(16, theme.Ansi.Length);
                AppTheme.Apply(theme); window.UpdateLayout();
                var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["AppOnAccent"]);
                foreach (var button in row.Children.Cast<IconButton>())
                {
                    var icon = Assert.IsType<PathIcon>(button.Content);
                    Assert.Equal(foreground.Color, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);
                    Assert.Null(icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Stroke);
                    Assert.True(icon.Data!.Bounds.Width > 0);
                }
                if (Environment.GetEnvironmentVariable("VIBE_QA_DIR") is { } qa)
                {
                    using var bitmap = new RenderTargetBitmap(new PixelSize(360, 90)); bitmap.Render(window);
                    bitmap.Save(System.IO.Path.Combine(qa, name.Replace(' ', '-') + "-icons.png"), PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally { window.Close(); AppTheme.Apply(original); }
    }
}
