using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace CodexManager.Tests;

public class InteractionReviewTests
{
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
            var result = palette.Open(owner);
            Assert.True(palette.IsVisible); Assert.True(owner.IsEnabled);
            owner.MouseDown(new Point(500, 480), MouseButton.Left); owner.MouseUp(new Point(500, 480), MouseButton.Left);
            Assert.Null(await result.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.False(palette.IsVisible);
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
