using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

namespace CodexManager.Tests;

public class IconUiTests
{
    [AvaloniaFact]
    public void CodiconsRenderUsingAvaloniaNativeControls()
    {
        var icons = new[] { "refresh", "add", "remove", "archive", "delete", "settings", "terminal", "permission", "warning", "chevron-down" };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(24) };
        foreach (var name in icons)
        {
            var icon = AppIcons.Create(name, name == "chevron-down" ? 8 : 16);
            Assert.NotNull(icon.Data); Assert.True(icon.Data.Bounds.Width > 0); Assert.True(icon.Data.Bounds.Height > 0);
            row.Children.Add(icon);
        }
        var window = new Window { Width = 380, Height = 80, Content = row }; window.Show();
        try
        {
            window.UpdateLayout();
            if (Environment.GetEnvironmentVariable("VIBE_QA_DIR") is { } qa)
            {
                using var screenshot = new RenderTargetBitmap(new PixelSize(380, 80)); screenshot.Render(window);
                screenshot.Save(Path.Combine(qa, "codicons-preview.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }
}
