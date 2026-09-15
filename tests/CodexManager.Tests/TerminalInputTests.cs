using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using SvcSystems.UI.Terminal;
using TerminalKey = XTerm.Input.Key;

namespace CodexManager.Tests;

public class TerminalInputTests
{
    [AvaloniaFact]
    public async Task MobileKeysSendRawInputAndRespectApplicationCursorMode()
    {
        var sent = new List<string>();
        using var view = new RemoteTerminalView(request =>
        {
            var method = request["method"]!.GetValue<string>();
            if (method == "terminal/open") return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = "shell" });
            if (method == "terminal/read") return Task.FromResult<JsonNode?>(new JsonObject { ["text"] = "", ["offset"] = 0L });
            if (method == "terminal/input") sent.Add(request["text"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(JsonValue.Create(true));
        }, mobile: true);
        var window = new Window { Content = view }; window.Show();
        try
        {
            await view.Open("workspace", "Terminal");
            Assert.Empty(view.GetLogicalDescendants().OfType<TextBox>());
            var bar = view.GetLogicalDescendants().OfType<Grid>().Single(g => g.Name == "TerminalKeyBar");
            Assert.Equal(14, bar.Children.Count);
            await view.SendKeystroke(new("hello"));
            Assert.Equal("hello", sent[^1]);
            await view.SendKeystroke(new(Key: TerminalKey.Enter)); Assert.Equal("\r", sent[^1]);
            var ctrl = bar.Children.OfType<Button>().Single(b => b.Name == "TerminalCtrl");
            ctrl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.SendKeystroke(new("c")); Assert.Equal("\u0003", sent[^1]);
            await view.SendKeystroke(new("c")); Assert.Equal("c", sent[^1]);
            await view.SendKeystroke(new(Key: TerminalKey.UpArrow)); Assert.Equal("\u001b[A", sent[^1]);
            var terminal = view.GetLogicalDescendants().OfType<ThemedTerminalControl>().Single();
            terminal.Model!.Feed("\u001b[?1h");
            await view.SendKeystroke(new(Key: TerminalKey.UpArrow)); Assert.Equal("\u001bOA", sent[^1]);
            view.SetSleeping(true);
            var count = sent.Count; Assert.False(await view.SendKeystroke(new("discard"))); Assert.Equal(count, sent.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CopyShortcutPreservesSelectionAndDoesNotSendControlC()
    {
        var model = new TerminalControlModel(new TerminalOptions());
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal }; window.Show();
        try
        {
            var sent = new List<string>(); model.UserInput += (_, e) => sent.Add(System.Text.Encoding.UTF8.GetString(e.Data.Span));
            model.Feed("COPY_THIS_TEXT\r\n"); terminal.SelectAll(); terminal.Focus();
            window.KeyPress(Key.LeftCtrl, Avalonia.Input.RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Assert.True(terminal.HasSelection);
            window.KeyPress(Key.C, Avalonia.Input.RawInputModifiers.Control, PhysicalKey.C, "c");
            await Task.Delay(30);
            using var clipboard = await window.Clipboard!.TryGetDataAsync();
            Assert.Contains("COPY_THIS_TEXT", await clipboard!.TryGetTextAsync());
            Assert.Empty(sent);
            model.ClearSelection();
            window.KeyPress(Key.C, Avalonia.Input.RawInputModifiers.Control, PhysicalKey.C, "c");
            Assert.Contains("\u0003", sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(Key.Insert, Avalonia.Input.RawInputModifiers.Shift, PhysicalKey.Insert)]
    [InlineData(Key.V, Avalonia.Input.RawInputModifiers.Control, PhysicalKey.V)]
    [InlineData(Key.V, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift, PhysicalKey.V)]
    public async Task PasteShortcutsPasteLoginCodeWithoutSubmittingIt(Key key, Avalonia.Input.RawInputModifiers modifiers, PhysicalKey physical)
    {
        var model = new TerminalControlModel(new TerminalOptions());
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal }; window.Show();
        try
        {
            var sent = ""; model.UserInput += (_, e) => sent += System.Text.Encoding.UTF8.GetString(e.Data.Span);
            await window.Clipboard!.SetTextAsync("test-login-code");
            terminal.Focus();
            window.KeyPress(key, modifiers, physical, "");
            for (var i = 0; i < 50 && sent.Length == 0; i++) await Task.Delay(10);
            Assert.Equal("test-login-code", sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PasteUsesBracketedPasteForInteractivePrograms()
    {
        var model = new TerminalControlModel(new TerminalOptions());
        var terminal = new ThemedTerminalControl { Model = model };
        var window = new Window { Content = terminal }; window.Show();
        try
        {
            var sent = ""; model.UserInput += (_, e) => sent += System.Text.Encoding.UTF8.GetString(e.Data.Span);
            model.Feed("\u001b[?2004h");
            await window.Clipboard!.SetTextAsync("first\nsecond");
            await terminal.PasteText();
            Assert.Equal("\u001b[200~first\rsecond\u001b[201~", sent);
        }
        finally { window.Close(); }
    }
}
