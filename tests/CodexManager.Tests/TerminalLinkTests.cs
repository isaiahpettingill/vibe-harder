using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using SvcSystems.UI.Terminal;

namespace CodexManager.Tests;

public class TerminalLinkTests
{
    [AvaloniaFact]
    public void LocalFilePathsAreUnderlinedButRemotePathsAreNot()
    {
        var model = new TerminalControlModel();
        var terminal = new ThemedTerminalControl { Model = model, FileWorkspace = new Workspace("w", "Test", "/home/me", "Debian") };
        var window = new Window { Content = terminal, Width = 900, Height = 300 }; window.Show();
        try
        {
            model.Feed("/home/me/main.cs:12:3\r\n");
            Assert.Equal(@"\\wsl.localhost\Debian\home\me\main.cs", terminal.LinkAt(Cell(terminal, 5, 0))?.LocalPath);
            Assert.Single(terminal.LinkUnderlines());
            var remote = new ThemedTerminalControl { Model = model }; window.Content = remote;
            Assert.Empty(remote.LinkUnderlines());
        }
        finally { window.Close(); }
    }

    private static Point Cell(ThemedTerminalControl terminal, int col, int row)
    {
        // Use the library's actual render metrics to verify our independent hit testing.
        var size = (Size)typeof(TerminalControl).GetField("_consoleTextSize", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
        return new Point((col + .5) * size.Width, (row + .5) * size.Height);
    }

    [AvaloniaFact]
    public void DetectsUrlsAcrossSoftWrapsAndScrollbackWithoutJoiningHardLines()
    {
        var model = new TerminalControlModel();
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal, Width = 420, Height = 200 }; window.Show();
        try
        {
            var url = "https://example.com/" + new string('a', model.Terminal.Cols * 2);
            model.Feed(url + "\r\n");
            Assert.Equal(url, terminal.LinkAt(Cell(terminal, 5, 0))?.AbsoluteUri);
            Assert.Equal(url, terminal.LinkAt(Cell(terminal, 5, 1))?.AbsoluteUri);
            Assert.True(terminal.LinkUnderlines().Count >= 3);
            for (var i = 0; i < 30; i++) model.Feed("filler\r\n");
            model.ScrollToYDisp(0);
            Assert.Equal(url, terminal.LinkAt(Cell(terminal, 5, 1))?.AbsoluteUri);
            Assert.True(terminal.LinkUnderlines().Count >= 3);
            model = new TerminalControlModel(); terminal.Model = model;
            model.Feed("https://example.com/\r\nseparate-line\r\n");
            Assert.Equal("https://example.com/", terminal.LinkAt(Cell(terminal, 5, 0))?.AbsoluteUri);
            Assert.Null(terminal.LinkAt(Cell(terminal, 5, 1)));
            Assert.Null(terminal.LinkAt(new Point(-1, 0)));
            Assert.Null(terminal.LinkAt(new Point(window.Width, 0)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HandlesUnicodePunctuationAndRejectsUnsafeSchemes()
    {
        var model = new TerminalControlModel();
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal, Width = 900, Height = 300 }; window.Show();
        try
        {
            model.Feed("界 (https://example.com/a_(b)).\r\nwww.example.com\r\nmailto:me@example.com\r\nfile:///tmp/test\r\njavascript:alert(1)\r\nhttps://localhost:1234/callback?code=abc&state=xyz\r\n");
            Assert.Equal("https://example.com/a_(b)", terminal.LinkAt(Cell(terminal, 8, 0))?.AbsoluteUri);
            Assert.Null(terminal.LinkAt(Cell(terminal, 29, 0)));
            Assert.Equal("https://www.example.com/", terminal.LinkAt(Cell(terminal, 4, 1))?.AbsoluteUri);
            Assert.Equal("mailto:me@example.com", terminal.LinkAt(Cell(terminal, 4, 2))?.AbsoluteUri);
            Assert.Null(terminal.LinkAt(Cell(terminal, 4, 3)));
            Assert.Null(terminal.LinkAt(Cell(terminal, 4, 4)));
            Assert.Equal("https://localhost:1234/callback?code=abc&state=xyz", terminal.LinkAt(Cell(terminal, 4, 5))?.AbsoluteUri);
            Assert.Equal(4, terminal.LinkUnderlines().Count);
            if (Environment.GetEnvironmentVariable("VIBE_QA_DIR") is { } qa)
            {
                window.UpdateLayout();
                using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 300));
                image.Render(window); image.Save(Path.Combine(qa, "terminal-underlines.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            model.Feed("\u001b[?1000h");
            Assert.Empty(terminal.LinkUnderlines());
            Assert.Null(terminal.LinkAt(Cell(terminal, 8, 0)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickLaunchesButDragAndTerminalMouseModeDoNot()
    {
        var model = new TerminalControlModel();
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal, Width = 500, Height = 200 }; window.Show();
        var popups = new List<Popup>();
        // Headless has a no-op launcher: its failure flyout proves the click reached it.
        using var subscription = Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) => { if (popup.IsOpen) popups.Add(popup); });
        try
        {
            model.Feed("https://example.com\r\n");
            var point = Cell(terminal, 5, 0);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Single(popups);
            popups[0].IsOpen = false;
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(Cell(terminal, 12, 0));
            window.MouseUp(Cell(terminal, 12, 0), MouseButton.Left);
            Assert.True(terminal.HasSelection);
            Assert.Single(popups);
            model.ClearSelection(); model.Feed("\u001b[?1000h");
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Single(popups);
            model.Feed("\u001b[?1000l");
            using var touch = new Avalonia.Input.Pointer(42, PointerType.Touch, true);
            terminal.RaiseEvent(new PointerPressedEventArgs(terminal, touch, terminal, point, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, touch, terminal, point, 2,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Assert.Equal(2, popups.Count);
        }
        finally { foreach (var popup in popups) popup.IsOpen = false; window.Close(); }
    }
}
