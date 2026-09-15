using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using TerminalKey = XTerm.Input.Key;
using TerminalModifiers = XTerm.Input.KeyModifiers;

namespace CodexManager;

public sealed class RemoteTerminalView : Grid, IDisposable
{
    private readonly Func<JsonObject, Task<JsonNode?>> call;
    private readonly TerminalControlModel model = new(new TerminalOptions { Cols = 80, Rows = 24, ReflowOnResize = false });
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private DateTimeOffset interactiveUntil;
    private readonly TextBlock status = new() { Text = "Connecting to the host terminal…", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly Queue<(string Id, string Text, TaskCompletionSource<bool> Completion)> pendingInput = new();
    private bool sendingInput;
    private readonly TerminalPrediction prediction = new();
    private readonly TextBlock predictedText = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ZIndex = 10 };
    private bool polling, disposed, sleeping;
    private readonly bool mobile;
    private readonly Grid keyBar = new() { Name = "TerminalKeyBar", RowDefinitions = new("Auto,Auto"), ColumnDefinitions = new("*,*,*,*,*,*,*"), IsEnabled = false };
    private readonly Action<TerminalKeystroke> keyboardReceiver;
    private readonly ThemedTerminalControl terminal;
    private readonly Button control = new() { Content = "CTRL", Name = "TerminalCtrl", Focusable = false };
    private readonly Button alt = new() { Content = "ALT", Name = "TerminalAlt", Focusable = false };
    private bool ctrlHeld, altHeld;
    public bool InputReady { get; private set; }
    private long offset;
    private (int Cols, int Rows) size;
    private string caption = "Remote terminal";
    public string? TerminalId { get; private set; }
    public event Action? Back;
    public RemoteTerminalView(Func<JsonObject, Task<JsonNode?>> call, bool? mobile = null)
    {
        this.call = call; this.mobile = mobile ?? OperatingSystem.IsAndroid();
        keyboardReceiver = input => _ = SendKeystroke(input);
        Name = "RemoteTerminal"; RowDefinitions = new("Auto,*,Auto"); RowSpacing = 6;
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        var back = new IconButton { Name = "TerminalBack", Icon = "chevron-left", Label = "Return to chat" }; back.Click += (_, _) => Back?.Invoke(); header.Children.Add(back);
        status.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(status, 1); header.Children.Add(status);
        var toggleKeyboard = new IconButton { Name = "TerminalKeyboard", Icon = "keyboard", Label = "Show keyboard", IsVisible = this.mobile, Focusable = false };
        toggleKeyboard.Click += (_, _) => FocusInput();
        terminal = new ThemedTerminalControl { Model = model, FontSize = this.mobile ? 12 : 13 };
        var copy = new IconButton { Icon = "copy", Label = "Copy selection", Focusable = false, IsVisible = false };
        copy.Click += async (_, _) => await terminal.CopyText();
        terminal.PropertyChanged += (_, e) => { if (e.Property == ThemedTerminalControl.HasSelectionProperty) copy.IsVisible = terminal.HasSelection; };
        var more = new IconButton { Icon = "more", Label = "Terminal actions", Focusable = false };
        more.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            void Item(string text, Func<Task> action) { var item = new MenuItem { Header = text }; item.Click += async (_, _) => { try { await action(); } catch (Exception error) { status.Text = AppDiagnostics.Message("Terminal action failed", error); } }; menu.Items.Add(item); }
            Item("Copy", terminal.CopyText); Item("Paste", terminal.PasteText);
            Item("Select all", () => { terminal.SelectAll(); return Task.CompletedTask; });
            Item("Interrupt (Ctrl+C)", async () => await Input(model.Terminal.Engine.GenerateCharInput('c', TerminalModifiers.Control)));
            Item("Close terminal", async () =>
            {
                if (TerminalId is not { } id) return;
                if (await call(new() { ["method"] = "terminal/close", ["terminalId"] = id }) is null) return;
                TerminalId = null; Ready(false); ReleaseKeyboard(); timer.Stop(); Back?.Invoke();
            });
            menu.ShowAt(more);
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { copy, toggleKeyboard, more } };
        Grid.SetColumn(actions, 2); header.Children.Add(actions); Children.Add(header);
        terminal.Bind(ThemedTerminalControl.FontFamilyProperty, this.GetResourceObservable("TerminalFont"));
        Grid.SetRow(terminal, 1); Children.Add(terminal);
        Grid.SetRow(predictedText, 1); Children.Add(predictedText);
        predictedText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AppForeground"));
        terminal.SizeChanged += (_, _) => ClearPrediction();
        terminal.AddHandler(PointerWheelChangedEvent, (_, _) => ClearPrediction(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        keyBar.IsVisible = this.mobile; Grid.SetRow(keyBar, 2); Children.Add(keyBar);
        var keys = new (string Label, TerminalKeystroke Stroke)[] {
            ("ESC", new(Key: TerminalKey.Escape)), ("/", new("/")), ("−", new("-")),
            ("HOME", new(Key: TerminalKey.Home)), ("↑", new(Key: TerminalKey.UpArrow)), ("END", new(Key: TerminalKey.End)), ("PGUP", new(Key: TerminalKey.PageUp)),
            ("TAB", new(Key: TerminalKey.Tab)), ("CTRL", default), ("ALT", default),
            ("←", new(Key: TerminalKey.LeftArrow)), ("↓", new(Key: TerminalKey.DownArrow)), ("→", new(Key: TerminalKey.RightArrow)), ("PGDN", new(Key: TerminalKey.PageDown)) };
        for (var i = 0; i < keys.Length; i++)
        {
            var (label, stroke) = keys[i];
            var button = label == "CTRL" ? control : label == "ALT" ? alt : new Button { Content = label, Name = "TerminalKey" + i, Focusable = false };
            button.FontSize = 11; button.Padding = new Thickness(2, 0); button.MinHeight = 40;
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(button, i % 7); Grid.SetRow(button, i / 7); keyBar.Children.Add(button);
            button.Click += async (_, _) =>
            {
                if (label == "CTRL") { ctrlHeld = !ctrlHeld; UpdateModifiers(); }
                else if (label == "ALT") { altHeld = !altHeld; UpdateModifiers(); }
                else await SendKeystroke(stroke);
                if (InputReady) MobileTerminalKeyboard.Current?.Focus(keyboardReceiver, false);
            };
        }
        Point? tapStart = null;
        terminal.AddHandler(PointerPressedEvent, (_, e) => tapStart = e.GetPosition(terminal), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        terminal.AddHandler(PointerReleasedEvent, (_, e) => { if (!terminal.HasSelection && terminal.LinkAt(e.GetPosition(terminal)) is null && tapStart is { } start && Math.Abs(e.GetPosition(terminal).X - start.X) < 12 && Math.Abs(e.GetPosition(terminal).Y - start.Y) < 12) FocusInput(); tapStart = null; }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        model.UserInput += async (_, e) =>
        {
            var text = Encoding.UTF8.GetString(e.Data.Span);
            if (this.mobile && (ctrlHeld || altHeld) && text.Length == 1) await SendKeystroke(new(text));
            else await Input(text);
        };
        // Protocol replies are produced by the host's terminal model, once per query.
        timer.Tick += async (_, _) => await Poll();
    }
    private void UpdateModifiers() { control.Classes.Set("accent", ctrlHeld); alt.Classes.Set("accent", altHeld); }
    public void FocusInput() { if (mobile && IsVisible && InputReady && !sleeping) MobileTerminalKeyboard.Current?.Focus(keyboardReceiver, true); }
    private void ReleaseKeyboard() { ClearPrediction(); MobileTerminalKeyboard.Current?.Release(keyboardReceiver); ctrlHeld = altHeld = false; UpdateModifiers(); }
    private void Ready(bool value) { InputReady = value; keyBar.IsEnabled = value; if (!value) ClearPrediction(); }
    public async Task<bool> SendKeystroke(TerminalKeystroke input)
    {
        var modifiers = input.Modifiers | (ctrlHeld ? TerminalModifiers.Control : 0) | (altHeld ? TerminalModifiers.Alt : 0);
        ctrlHeld = altHeld = false; UpdateModifiers();
        if (terminal.HasSelection && input.Text is "c" or "C" && modifiers.HasFlag(TerminalModifiers.Control)) { await terminal.CopyText(); return true; }
        var text = input.Key is { } key ? model.Terminal.Engine.GenerateKeyInput(key, modifiers)
            : string.Concat((input.Text ?? "").Select(c => model.Terminal.Engine.GenerateCharInput(c, modifiers)));
        model.EnsureCaretIsVisible();
        return text.Length > 0 && await Input(text);
    }
    public async Task Open(string workspaceId, string caption)
    {
        this.caption = caption;
        Ready(false); ReleaseKeyboard(); TerminalId = null; offset = 0; model.Feed("\u001bc");
        var result = await call(new() { ["method"] = "terminal/open", ["workspaceId"] = workspaceId });
        TerminalId = result?["id"]?.GetValue<string>(); Ready(TerminalId is not null);
        status.Text = TerminalId is null ? "Could not open terminal. Reconnect and try again." : caption;
        if (disposed) return;
        size = default; if (IsVisible && !sleeping) timer.Start(); await Poll();
    }
    public void SetSleeping(bool value) { sleeping = value; if (value) ReleaseKeyboard(); if (!value && IsVisible && !disposed) timer.Start(); else timer.Stop(); }
    public void SetVisible(bool visible) { IsVisible = visible; if (!visible) ReleaseKeyboard(); if (visible && !disposed && !sleeping) timer.Start(); else timer.Stop(); }
    private Task<bool> Input(string text)
    {
        var id = TerminalId;
        if (!InputReady || disposed || sleeping || !IsVisible || id is null) return Task.FromResult(false);
        var buffer = model.Terminal.Buffer;
        prediction.Input(text, buffer.X, buffer.YBase + buffer.Y, model.Terminal.Cols, PredictionEligible());
        RenderPrediction();
        interactiveUntil = DateTimeOffset.UtcNow.AddSeconds(2); timer.Interval = TimeSpan.FromMilliseconds(33);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingInput.Enqueue((id, text, completion));
        if (!sendingInput) _ = DrainInput();
        return completion.Task;
    }
    private async Task DrainInput()
    {
        sendingInput = true;
        try
        {
            while (pendingInput.Count > 0)
            {
                var batch = new List<TaskCompletionSource<bool>>(); var text = new StringBuilder(); var id = pendingInput.Peek().Id;
                while (pendingInput.TryPeek(out var next) && next.Id == id && (text.Length == 0 || text.Length + next.Text.Length <= 65536))
                { pendingInput.Dequeue(); text.Append(next.Text); batch.Add(next.Completion); }
                var sent = false;
                try
                {
                    if (InputReady && !disposed && !sleeping && IsVisible && id == TerminalId)
                    {
                        sent = await call(new() { ["method"] = "terminal/input", ["terminalId"] = id, ["text"] = text.ToString() }) is not null;
                        if (!sent) { ClearPrediction(); Ready(false); status.Text = "Connection interrupted. Reconnecting…"; }
                    }
                }
                catch (Exception error) { ClearPrediction(); Ready(false); status.Text = AppDiagnostics.Message("Terminal input failed", error); }
                finally { foreach (var completion in batch) completion.TrySetResult(sent); }
            }
        }
        finally { sendingInput = false; }
    }
    private async Task Poll()
    {
        if (polling || sleeping || disposed || TerminalId is not { } id || !IsVisible) return;
        polling = true;
        try
        {
            var next = (model.Terminal.Cols, model.Terminal.Rows);
            if (size != next) { if (await call(new() { ["method"] = "terminal/resize", ["terminalId"] = id, ["cols"] = next.Cols, ["rows"] = next.Rows }) is not null) size = next; }
            var result = await call(new() { ["method"] = "terminal/read", ["terminalId"] = id, ["offset"] = offset });
            if (disposed || id != TerminalId) return;
            if (result is null) { Ready(false); status.Text = "Reconnecting to the host terminal…"; return; }
            if (result["error"] is { } error) { TerminalId = null; Ready(false); status.Text = error.GetValue<string>(); timer.Stop(); return; }
            Ready(true); status.Text = caption;
            if (result["reset"]?.GetValue<bool>() == true) model.Feed("\u001bc");
            var output = result["text"]!.GetValue<string>();
            model.Feed(output); offset = result["offset"]!.GetValue<long>();
            var buffer = model.Terminal.Buffer;
            prediction.Reconcile(buffer.X, buffer.YBase + buffer.Y, col => buffer.GetLine(buffer.YBase + buffer.Y)?[col].Content ?? "", PredictionEligible());
            RenderPrediction();
            if (output.Length > 0) interactiveUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            timer.Interval = TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow < interactiveUntil ? 33 : 150);
            if (result["exited"]?.GetValue<bool>() == true) { Ready(false); TerminalId = null; ReleaseKeyboard(); status.Text = "Shell exited. Reopen the terminal to start a new shell."; timer.Stop(); }
        }
        catch (Exception error) { if (!disposed) status.Text = AppDiagnostics.Message("Terminal refresh failed", error); }
        finally { polling = false; }
    }
    public void Dispose() { disposed = true; timer.Stop(); ReleaseKeyboard(); Back = null; }
    private bool PredictionEligible() => !model.Terminal.Engine.IsAlternateBufferActive && !model.IsMouseModeActive && !terminal.HasSelection && model.ScrollOffset == model.Terminal.Buffer.YBase;
    private void ClearPrediction() { prediction.Reset(); predictedText.Text = ""; }
    private void RenderPrediction()
    {
        var (origin, cell) = terminal.PredictionMetrics();
        predictedText.FontFamily = terminal.FontFamily; predictedText.FontSize = terminal.FontSize;
        predictedText.Margin = new Thickness(origin.X + prediction.Column * cell.Width, origin.Y + (prediction.Row - model.ScrollOffset) * cell.Height, 0, 0);
        predictedText.Text = prediction.Visible;
    }
}
