using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace CodexManager.Tests;

public class InteractionReviewTests
{
    [AvaloniaFact]
    public async Task LoginPasteButtonSendsClipboardToTheLoginTerminal()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "login-paste-" + Guid.NewGuid().ToString("N")));
        var view = new MainView(store, [], []);
        var window = new Window { Content = view }; view.AttachDesktop(window); window.Show();
        try
        {
            var model = new SvcSystems.UI.Terminal.TerminalControlModel();
            var terminal = new ThemedTerminalControl { Model = model }; var sent = "";
            model.UserInput += (_, e) => sent += Encoding.UTF8.GetString(e.Data.Span);
            view.FindControl<ContentControl>("LoginTerminalHost")!.Content = terminal;
            view.FindControl<Border>("LoginPanel")!.IsVisible = true;
            view.FindControl<ContentControl>("LoginTerminalHost")!.IsVisible = true;
            view.FindControl<Button>("PasteLoginButton")!.IsVisible = true;
            window.UpdateLayout();
            await window.Clipboard!.SetTextAsync("test-sign-in-code");
            view.FindControl<Button>("PasteLoginButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 50 && sent.Length == 0; i++) await Task.Delay(10);
            Assert.Equal("test-sign-in-code", sent);
        }
        finally { view.RequestExit(); await Task.Delay(200, TestContext.Current.CancellationToken); }
    }

    [AvaloniaFact]
    public async Task LocalWorkspaceRestoresTerminalSelectionWidthAndVisibility()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "terminal-workspaces-" + Guid.NewGuid().ToString("N")));
        var view = new MainView(store, [], []);
        var window = new Window { Content = view }; view.AttachDesktop(window); window.Show();
        try
        {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sessions = (Dictionary<string, List<(TabItem Tab, TerminalSession Session)>>)typeof(MainView).GetField("terminals", flags)!.GetValue(view)!;
        using var first = new TerminalSession(); using var second = new TerminalSession();
        var firstTab = new TabItem(); var secondTab = new TabItem();
        sessions["one"] = [(firstTab, first), (secondTab, second)];
        var change = typeof(MainView).GetMethod("SwitchTerminalWorkspace", flags)!;
        var tabs = view.FindControl<TabControl>("TerminalTabs")!; var drawer = view.FindControl<Grid>("TerminalDrawer")!;
        change.Invoke(view, ["one"]); tabs.SelectedItem = secondTab; drawer.Width = 300; drawer.IsVisible = true;
        change.Invoke(view, ["two"]); Assert.False(drawer.IsVisible); Assert.Equal(0, tabs.ItemCount);
        Assert.True(view.FindControl<StackPanel>("TerminalEmptyState")!.IsVisible);
        change.Invoke(view, ["one"]); Assert.True(drawer.IsVisible); Assert.Same(secondTab, tabs.SelectedItem); Assert.Equal(300, drawer.Width);
        Assert.False(view.FindControl<StackPanel>("TerminalEmptyState")!.IsVisible);
        drawer.IsVisible = false; change.Invoke(view, ["two"]); change.Invoke(view, ["one"]); Assert.False(drawer.IsVisible);
        }
        finally { view.RequestExit(); await Task.Delay(200, TestContext.Current.CancellationToken); }
    }

    [AvaloniaFact]
    public async Task PredictionIsVisibleWhileInputWaitsForTheHost()
    {
        var reply = new TaskCompletionSource<JsonNode?>(); var output = ""; var inputs = 0;
        Task<JsonNode?> Call(JsonObject r)
        {
            var method = r["method"]!.GetValue<string>();
            if (method == "terminal/input") { if (++inputs == 1) output = "a"; else return reply.Task; }
            JsonNode result = method switch { "terminal/open" => new JsonObject { ["id"] = "s" }, "terminal/read" => new JsonObject { ["text"] = output, ["offset"] = 0L }, _ => JsonValue.Create(true)! };
            if (method == "terminal/read") output = "";
            return Task.FromResult<JsonNode?>(result);
        }
        using var terminal = new RemoteTerminalView(Call);
        var window = new Window { Width = 800, Height = 500, Content = terminal }; window.Show(); window.UpdateLayout();
        try
        {
            await terminal.Open("w", "Host"); await terminal.SendKeystroke(new("a"));
            var poll = typeof(RemoteTerminalView).GetMethod("Poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)poll.Invoke(terminal, null)!;
            var pending = terminal.SendKeystroke(new("b"));
            try { Assert.False(pending.IsCompleted); Assert.Contains(terminal.Children.OfType<TextBlock>(), t => t.Text == "b"); }
            finally { reply.TrySetResult(JsonValue.Create(true)); await pending; }
            output = "b"; await (Task)poll.Invoke(terminal, null)!;
            Assert.DoesNotContain(terminal.Children.OfType<TextBlock>(), t => t.Text == "b");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void PredictionWaitsForEchoAndReconcilesWithoutDuplicatingText()
    {
        var p = new TerminalPrediction();
        p.Input("a", 0, 0, 80, true); Assert.Empty(p.Visible);
        p.Reconcile(1, 0, _ => "a", true);
        p.Input("bc", 1, 0, 80, true); Assert.Equal("bc", p.Visible);
        p.Reconcile(2, 0, _ => "b", true); Assert.Equal("c", p.Visible);
        p.Reconcile(3, 0, _ => "c", true); Assert.Empty(p.Visible);
        p.Input("\r", 3, 0, 80, true);
        p.Input("secret", 0, 1, 80, true); p.Reconcile(0, 1, _ => "", true); Assert.Empty(p.Visible);
        p.Reset(); p.Input("a", 0, 0, 80, true); p.Reconcile(1, 0, _ => "*", true); Assert.False(p.Confirmed);
    }

    [AvaloniaFact]
    public void RemoteWorkspaceRestoresTerminalViewAndVisibility()
    {
        using var remote = new RemoteView(new RemoteHost("Test", "127.0.0.1", 1, "", ""));
        var window = new Window { Width = 960, Height = 650, Content = remote }; window.Show();
        try
        {
            var change = (Action<string>)typeof(RemoteView).GetField("switchTerminalWorkspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(remote)!;
            var layout = Assert.IsType<Grid>(remote.Content);
            change("one"); var one = layout.Children.OfType<RemoteTerminalView>().Single(); one.SetVisible(true);
            change("two"); var two = layout.Children.OfType<RemoteTerminalView>().Single();
            Assert.NotSame(one, two); Assert.False(two.IsVisible); Assert.False(one.IsVisible);
            change("one"); Assert.Same(one, layout.Children.OfType<RemoteTerminalView>().Single()); Assert.True(one.IsVisible);
            change("two"); Assert.Same(two, layout.Children.OfType<RemoteTerminalView>().Single()); Assert.False(two.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RemoteTerminalDocksOnLaptopAndOverlaysOnlyOnNarrowWindows()
    {
        using var remote = new RemoteView(new RemoteHost("Test", "127.0.0.1", 1, "", ""));
        var root = new Grid { ColumnDefinitions = new("280,*") };
        Grid.SetColumn(remote, 1); root.Children.Add(remote);
        var window = new Window { Width = 960, Height = 650, Content = root }; window.Show();
        try
        {
            window.UpdateLayout();
            var layout = Assert.IsType<Grid>(remote.Content);
            var terminal = layout.Children.OfType<RemoteTerminalView>().Single();
            terminal.SetVisible(true); window.UpdateLayout();
            Assert.Equal(2, Grid.GetColumn(terminal));
            Assert.True(layout.Children[0].Bounds.Width > 200);
            Assert.True(terminal.Bounds.Width > 200);
            Assert.True(terminal.Bounds.X >= layout.Children[0].Bounds.Right);
            window.Width = 600; Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(0, Grid.GetColumn(terminal));
            window.Width = 960; Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(2, Grid.GetColumn(terminal));
            terminal.SetVisible(false); window.UpdateLayout();
            Assert.Equal(0, layout.ColumnDefinitions[2].ActualWidth);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoteTerminalBatchesTypingWhilePreviousInputIsInFlight()
    {
        var first = new TaskCompletionSource<JsonNode?>(); var sent = new List<string>();
        Task<JsonNode?> Call(JsonObject request)
        {
            if (request["method"]!.GetValue<string>() == "terminal/input")
            { sent.Add(request["text"]!.GetValue<string>()); return sent.Count == 1 ? first.Task : Task.FromResult<JsonNode?>(JsonValue.Create(true)); }
            return Task.FromResult<JsonNode?>(request["method"]!.GetValue<string>() switch
            {
                "terminal/open" => new JsonObject { ["id"] = "shell" },
                "terminal/read" => new JsonObject { ["text"] = "", ["offset"] = 0L },
                _ => JsonValue.Create(true)
            });
        }
        using var terminal = new RemoteTerminalView(Call);
        await terminal.Open("w", "Host");
        var a = terminal.SendKeystroke(new("a"));
        var b = terminal.SendKeystroke(new("b"));
        var c = terminal.SendKeystroke(new("c"));
        Assert.Single(sent);
        first.SetResult(JsonValue.Create(true));
        Assert.All(await Task.WhenAll(a, b, c), Assert.True);
        Assert.Equal(new[] { "a", "bc" }, sent);
    }

    [AvaloniaFact]
    public async Task TerminalKeyboardStreamsTypingBackspaceAndEnter()
    {
        var input = new StringBuilder();
        Task<JsonNode?> Call(JsonObject request)
        {
            if (request["method"]!.GetValue<string>() == "terminal/input") input.Append(request["text"]!.GetValue<string>());
            return Task.FromResult<JsonNode?>(request["method"]!.GetValue<string>() switch
            {
                "terminal/open" => new JsonObject { ["id"] = "shell" },
                "terminal/read" => new JsonObject { ["text"] = "", ["offset"] = 0L },
                _ => JsonValue.Create(true)
            });
        }
        using var terminal = new RemoteTerminalView(Call);
        await terminal.Open("workspace", "Host");
        await terminal.SendKeystroke(new("echx"));
        await terminal.SendKeystroke(new(Key: XTerm.Input.Key.Backspace));
        await terminal.SendKeystroke(new("o hé"));
        await terminal.SendKeystroke(new(Key: XTerm.Input.Key.Enter));
        Assert.Equal("echx\x7fo hé\r", input.ToString());
    }

    [AvaloniaFact]
    public void TranscriptPagingPreservesReadingPositionAndLatestAppearsOnlyWhenAway()
    {
        var messages = Enumerable.Range(0, 250).Select(i => new Message { Sequence = i, Text = "Message " + i }).ToArray();
        var list = new ListBox { ItemsSource = messages.Skip(50).ToArray(), ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new TranscriptPanel()), ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Message>((m, _) => new TextBlock { Text = m!.Text, Height = 60 }) };
        var latest = new IconButton();
        var navigation = new TranscriptNavigation(list, latest, () => false, _ => Task.CompletedTask);
        var window = new Window { Width = 500, Height = 400, Content = list }; window.Show();
        try
        {
            window.UpdateLayout(); list.ScrollIntoView(messages[^1]); window.UpdateLayout(); navigation.Update();
            Assert.False(latest.IsVisible);
            list.Scroll!.Offset = new Vector(0, list.Scroll.Offset.Y - 100); window.UpdateLayout(); navigation.Update();
            Assert.False(latest.IsVisible);
            list.Scroll.Offset = new Vector(0, 100); window.UpdateLayout(); navigation.Update(); Assert.True(latest.IsVisible);
            var panel = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<TranscriptPanel>().Single();
            var anchor = panel.CaptureAnchor();
            TranscriptNavigation.ReplacePage(list, messages.Take(200).ToArray()); window.UpdateLayout();
            Assert.Equal(anchor, panel.CaptureAnchor());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PaletteDismissesOnOutsideActivationAndLauncherKeepsItsSize()
    {
        var button = new IconButton { Icon = "command", Label = "Command palette" };
        var owner = new Window { Width = 600, Height = 500, Content = new StackPanel { Children = { button } } }; owner.Show();
        try
        {
            owner.UpdateLayout(); var bounds = button.Bounds;
            for (var i = 0; i < 3; i++) { owner.MouseMove(new Point(8, 8)); owner.UpdateLayout(); owner.MouseMove(new Point(400, 400)); owner.UpdateLayout(); }
            Assert.Equal(bounds.Size, button.Bounds.Size);
            var palette = new CommandPalette([new("Test", "A command", () => Task.CompletedTask)], null);
            var overlayHost = new Grid(); owner.Content = overlayHost;
            var result = palette.Open(overlayHost);
            Assert.True(palette.IsVisible); Assert.True(owner.IsEnabled);
            owner.UpdateLayout();
            Assert.Same(owner, TopLevel.GetTopLevel(palette));
            if (Environment.GetEnvironmentVariable("VIBE_QA_DIR") is { } qa)
            {
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 500)); bitmap.Render(owner);
                bitmap.Save(Path.Combine(qa, "palette-overlay.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            owner.MouseDown(new Point(5, 5), MouseButton.Left); owner.MouseUp(new Point(5, 5), MouseButton.Left);
            Assert.Null(await result.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.False(palette.IsVisible);
            var second = new CommandPalette([new("Test", "A command", () => Task.CompletedTask)], null);
            var escaped = second.Open(overlayHost);
            owner.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            owner.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            Assert.Null(await escaped.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(second, overlayHost.Children);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoteTerminalRunsOnHostResizesAndCloses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remote-terminal-" + Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        using var service = new SessionService(store, [new Workspace("w", "Test", directory)], [], (_, _) => throw new InvalidOperationException());
        var opened = await service.Handle(new() { ["method"] = "terminal/open", ["workspaceId"] = "w" });
        var id = opened!["id"]!.GetValue<string>();
        JsonObject Request(string method) => new() { ["method"] = "terminal/" + method, ["terminalId"] = id };
        var resize = Request("resize"); resize["cols"] = 45; resize["rows"] = 18; await service.Handle(resize);
        var command = Request("input"); command["text"] = OperatingSystem.IsWindows() ? "Write-Output ('REMOTE_RESULT_' + 42); (Get-Location).Path\r" : "printf 'REMOTE_RESULT_%s\\n' 42; pwd\r";
        // ConPTY performs its cursor handshake before accepting a shell command.
        await Task.Delay(1500, TestContext.Current.CancellationToken); await service.Handle(command);
        var output = new StringBuilder(); long offset = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!output.ToString().Contains("REMOTE_RESULT_42") || !output.ToString().Contains(directory))
        {
            var read = Request("read"); read["offset"] = offset;
            var response = await service.Handle(read); output.Append(response!["text"]!.GetValue<string>()); offset = response["offset"]!.GetValue<long>();
            await Task.Delay(50, timeout.Token);
        }
        await service.Handle(Request("close"));
        await Assert.ThrowsAsync<IOException>(() => service.Handle(Request("read")));
        await Assert.ThrowsAsync<IOException>(() => service.Handle(new() { ["method"] = "terminal/open", ["workspaceId"] = "missing" }));
    }

    [AvaloniaFact]
    public async Task RemoteQueueCanSteerByStableIdWithoutLosingOtherMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remote-queue-" + Guid.NewGuid().ToString("N"));
        using var store = new Store(directory); var workspace = new Workspace("w", "Test", directory); var chat = new Chat { Id = "c", WorkspaceId = "w" };
        store.Save(workspace); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --steering");
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        var running = runtime.Send("hang", []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!chat.Messages.Any(m => m.Text == "Working")) await Task.Delay(20, timeout.Token);
        foreach (var text in new[] { "first", "second" }) await service.Handle(new() { ["method"] = "send", ["chatId"] = "c", ["text"] = text });
        var snapshot = await service.Handle(new() { ["method"] = "chat", ["chatId"] = "c" });
        Assert.True(snapshot!["canSteer"]!.GetValue<bool>());
        var id = snapshot["queue"]![0]!["id"]!.GetValue<string>();
        await service.Handle(new() { ["method"] = "queue/steer", ["chatId"] = "c", ["queueId"] = id });
        Assert.Equal("second", Assert.Single(chat.QueuedInputs).Text);
        Assert.Contains(chat.Messages, m => m.Text == "first");
        await Assert.ThrowsAsync<IOException>(() => service.Handle(new() { ["method"] = "queue/remove", ["chatId"] = "c", ["queueId"] = id }));
        await runtime.Stop(); await running;
    }
}
