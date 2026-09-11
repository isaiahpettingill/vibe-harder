using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SvcSystems.UI.Terminal;

namespace CodexManager.Tests;

public class TerminalAppearanceTests
{
    [AvaloniaFact]
    public async Task PalettesUpdateAnOpenTerminalAndPersistSelection()
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")));
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-themes", Guid.NewGuid().ToString("N")));
        FontSettings.Apply(store); AppTheme.Apply(store);
        var model = new TerminalControlModel();
        var terminal = new ThemedTerminalControl { Model = model, FontFamily = FontSettings.Family(FontSettings.DefaultName), FontSize = 13, Height = 180 };
        var picker = AppTheme.Picker(store);
        var markdown = new ChatMarkdown { Text = "Chat with `inline code`\n\n```sh\nprintf 'hello'\n```" };
        var window = new Window { Width = 600, Height = 550, Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { picker, markdown, terminal } } }; window.Show();
        model.Feed("Default text\r\n\u001b[31mRed \u001b[32mGreen \u001b[34mBlue\u001b[0m");
        Assert.Equal(1, model.Terminal.Buffer.Lines[1]![0].Attributes.GetFgColor());
        var original = Application.Current!.Resources["SvcSystems.UI.TerminalColor0"];
        try
        {
            foreach (var palette in AppTheme.All)
            {
                picker.SelectedItem = palette; await Task.Delay(60);
                Assert.Equal(palette.Name, store.Setting("theme") ?? "Original");
                Assert.Equal(Color.Parse(palette.Background), Assert.IsType<SolidColorBrush>(Application.Current.Resources["SvcSystems.UI.TerminalColor0"]).Color);
                Assert.Same(original, Application.Current.Resources["SvcSystems.UI.TerminalColor0"]);
                Assert.Same(model, terminal.Model);
                var resolve = typeof(TerminalControl).GetMethod("ResolveColorBrush", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                Assert.Equal(Color.Parse(palette.Ansi[1]), Assert.IsAssignableFrom<ISolidColorBrush>(resolve.Invoke(terminal, [1])).Color);
                using var frame = new RenderTargetBitmap(new PixelSize(600, 550)); frame.Render(window);
                frame.Save(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/theme-" + palette.Name.Replace(' ', '-') + ".png")), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); AppTheme.Apply(AppTheme.All[0]); }
    }

    [AvaloniaFact]
    public async Task WslClearAndResizeKeepOneLivePrompt()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_WSL") != "1") return;
        using var store = new Store(Path.Combine(Path.GetTempPath(), "codex-terminal-redraw", Guid.NewGuid().ToString("N")));
        store.Setting("terminalCommand:wsl:Debian", "exec zsh -f");
        using var session = new TerminalSession();
        await session.Start(new Workspace("w", "test", "/tmp", "Debian"), settings: store);
        await Task.Delay(1500);
        session.Model.Send("PS1='REDRAW> '; printf 'TERM_CHECK=%s\\n' $TERM\r");
        await Task.Delay(500);
        Assert.Contains("TERM_CHECK=xterm-256color", session.Output.Text);
        session.Model.Send("clear\r"); await Task.Delay(500);
        string Viewport() => string.Join("\n", Enumerable.Range(0, session.Model.Terminal.Rows).Select(i => session.Model.Terminal.Buffer.Lines[session.Model.Terminal.Buffer.YDisp + i]?.TranslateToString(true)));
        Assert.DoesNotContain("TERM_CHECK", Viewport());
        for (var i = 0; i < 20; i++) { session.Model.Resize(640 + i * 8, 384, 8, 16); await Task.Delay(35); }
        await Task.Delay(700);
        var screen = Viewport();
        Assert.Equal(1, screen.Split("REDRAW>").Length - 1);
        session.Model.Send("printf 'STILL_%s\\n' alive\r"); await Task.Delay(500);
        Assert.Contains("STILL_alive", session.Output.Text);
    }
}
