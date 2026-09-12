using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;

namespace CodexManager;

public sealed class RemoteTerminalView : Grid, IDisposable
{
    private readonly Func<JsonObject, Task<JsonNode?>> call;
    private readonly TerminalControlModel model = new(new TerminalOptions { Cols = 80, Rows = 24, ReflowOnResize = false });
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly TextBlock status = new() { Text = "Connecting to the host terminal…", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly SemaphoreSlim inputGate = new(1);
    private bool polling, disposed, sleeping;
    private long offset;
    private (int Cols, int Rows) size;
    public string? TerminalId { get; private set; }
    public event Action? Back;
    public RemoteTerminalView(Func<JsonObject, Task<JsonNode?>> call)
    {
        this.call = call;
        Name = "RemoteTerminal"; RowDefinitions = new("Auto,*,Auto"); RowSpacing = 6;
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        var back = new IconButton { Icon = "chevron-left", Label = "Return to chat" }; back.Click += (_, _) => Back?.Invoke(); header.Children.Add(back);
        status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 1); header.Children.Add(status);
        var interrupt = new IconButton { Icon = "stop", Label = "Interrupt command (Ctrl+C)" }; interrupt.Click += async (_, _) => await Input("\u0003"); Grid.SetColumn(interrupt, 2); header.Children.Add(interrupt); Children.Add(header);
        var terminal = new ThemedTerminalControl { Model = model, FontSize = OperatingSystem.IsAndroid() ? 12 : 13 };
        terminal.Bind(ThemedTerminalControl.FontFamilyProperty, this.GetResourceObservable("TerminalFont"));
        Grid.SetRow(terminal, 1); Children.Add(terminal);
        // A TextBox supplies Android's native IME; physical keys still go directly to the terminal.
        var line = new TextBox { Name = "TerminalInput", PlaceholderText = "Terminal input…", IsVisible = OperatingSystem.IsAndroid() };
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), IsVisible = OperatingSystem.IsAndroid() }; footer.Children.Add(line);
        var enter = new IconButton { Icon = "send", Label = "Send terminal input" }; Grid.SetColumn(enter, 1); footer.Children.Add(enter); Grid.SetRow(footer, 2); Children.Add(footer);
        async Task Submit() { var text = line.Text ?? ""; if (await Input(text + "\r") && line.Text == text) line.Text = ""; }
        enter.Click += async (_, _) => await Submit();
        line.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Submit(); } };
        model.UserInput += async (_, e) => await Input(Encoding.UTF8.GetString(e.Data.Span));
        // Protocol replies are produced by the host's terminal model, once per query.
        timer.Tick += async (_, _) => await Poll();
    }
    public async Task Open(string workspaceId, string caption)
    {
        if (TerminalId is { } previous) await call(new() { ["method"] = "terminal/close", ["terminalId"] = previous });
        TerminalId = null; offset = 0; model.Feed("\u001bc");
        var result = await call(new() { ["method"] = "terminal/open", ["workspaceId"] = workspaceId });
        TerminalId = result?["id"]?.GetValue<string>();
        status.Text = TerminalId is null ? "Could not open terminal. Reconnect and try again." : caption;
        if (disposed) { if (TerminalId is { } id) await call(new() { ["method"] = "terminal/close", ["terminalId"] = id }); return; }
        size = default; timer.Start(); await Poll();
    }
    public void SetSleeping(bool value) { sleeping = value; if (!value && IsVisible && !disposed) timer.Start(); else timer.Stop(); }
    public void SetVisible(bool visible) { IsVisible = visible; if (visible && !disposed) timer.Start(); else timer.Stop(); }
    private async Task<bool> Input(string text)
    {
        await inputGate.WaitAsync();
        try { return !disposed && TerminalId is { } id && await call(new() { ["method"] = "terminal/input", ["terminalId"] = id, ["text"] = text }) is not null; }
        finally { inputGate.Release(); }
    }
    private async Task Poll()
    {
        if (polling || sleeping || disposed || TerminalId is not { } id || !IsVisible) return;
        polling = true;
        try
        {
            var next = (model.Terminal.Cols, model.Terminal.Rows);
            if (size != next) { await call(new() { ["method"] = "terminal/resize", ["terminalId"] = id, ["cols"] = next.Cols, ["rows"] = next.Rows }); size = next; }
            var result = await call(new() { ["method"] = "terminal/read", ["terminalId"] = id, ["offset"] = offset });
            if (disposed || id != TerminalId) return;
            if (result is null) { TerminalId = null; status.Text = "Terminal disconnected. Return to chat and reconnect."; timer.Stop(); return; }
            if (result["reset"]?.GetValue<bool>() == true) model.Feed("\u001bc");
            model.Feed(result["text"]!.GetValue<string>()); offset = result["offset"]!.GetValue<long>();
        }
        finally { polling = false; }
    }
    public void Dispose() { disposed = true; timer.Stop(); Back = null; }
}
