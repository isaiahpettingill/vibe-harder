using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class SidebarCustomizationTests
{
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
