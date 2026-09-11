using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CodexManager.Tests;

public class LiveTests
{
    [AvaloniaFact]
    public async Task LiveWindowsAndWslSendThroughAppAndResume()
    {
        if (Environment.GetEnvironmentVariable("CODEX_MANAGER_TEST_LIVE") != "1") return;
        foreach (var distro in new string?[] { null, "Debian" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-manager-live", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
            using (var store = new Store(directory)) store.Save(new Workspace("live", "Live smoke", distro is null ? directory : "/tmp/codex-manager-smoke", distro));
            var window = new MainWindow(); window.Show();
            await UiTests.NewChat(window, "live");
            var composer = window.FindControl<TextBox>("Composer")!;
            var draftChat = Assert.IsType<Chat>(UiTests.Named<ListBox>(window, "Chats_live").SelectedItem);
            using var image = new RenderTargetBitmap(new PixelSize(64, 64));
            using (var drawing = image.CreateDrawingContext()) drawing.FillRectangle(Brushes.Blue, new Rect(0, 0, 64, 64));
            using var imageBytes = new MemoryStream(); image.Save(imageBytes, PngBitmapEncoderOptions.Default);
            draftChat.Attachments.Add(new("blue.png", "image/png", Convert.ToBase64String(imageBytes.ToArray())));
            draftChat.Attachments.Add(new("reference.txt", "text/plain", "The file word is SKYFILE.", Path.Combine(directory, "reference.txt")));
            composer.Text = "Remember the test word MARIGOLD. Inspect the attached image and embedded text resource. Reply with MARIGOLD, the image's dominant color in uppercase, and the file word, separated by spaces. Do not use tools.";
            window.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForTurn(window);
            var chat = Assert.IsType<Chat>(UiTests.Named<ListBox>(window, "Chats_live").SelectedItem);
            Assert.Equal("Ready", chat.Status); Assert.NotNull(chat.SessionId);
            Assert.Contains(chat.Messages, m => m.Role == "assistant" && m.Text.Contains("MARIGOLD"));
            Assert.Contains(chat.Messages, m => m.Role == "assistant" && m.Text.Contains("BLUE") && m.Text.Contains("SKYFILE"));
            var sessionId = chat.SessionId;
            window.Close(); await Task.Delay(300);
            var reopened = new MainWindow(); reopened.Show();
            reopened.FindControl<TextBox>("Composer")!.Text = "What was the test word? Reply only with that word. Do not use tools.";
            reopened.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForTurn(reopened);
            var resumed = Assert.IsType<Chat>(UiTests.Named<ListBox>(reopened, "Chats_live").SelectedItem);
            Assert.Equal(sessionId, resumed.SessionId); Assert.Equal("Ready", resumed.Status);
            Assert.Equal(2, resumed.Messages.Count(m => m.Role == "assistant" && m.Text.Contains("MARIGOLD")));
            reopened.FindControl<TextBox>("Composer")!.Text = "Write every integer from 1 to 2000, one per line. Do not use tools.";
            var previousCount = resumed.Messages.Count;
            reopened.FindControl<Button>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var stopDeadline = DateTime.UtcNow.AddSeconds(40);
            while (resumed.Busy && (resumed.Messages.Count <= previousCount + 1 || resumed.Messages.Last().Text.Length == 0) && DateTime.UtcNow < stopDeadline) await Task.Delay(50);
            Assert.True(resumed.Busy, "Expected a streaming turn to interrupt");
            reopened.FindControl<Button>("StopButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForTurn(reopened);
            Assert.Equal("Interrupted", resumed.Status);
            reopened.Close(); await Task.Delay(300);
            // Delete only the disposable session created by this test, then prove it cannot resume.
            var owner = new Workspace("live", "Live smoke", distro is null ? directory : "/tmp/codex-manager-smoke", distro);
            await ChatHistory.DeleteFromCodex(owner, sessionId!);
            await using var verify = new AcpClient(Hosts.Agent(owner, Hosts.DefaultAdapter));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await verify.Initialize(timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => verify.Request("session/load", RpcJson.Object(("sessionId", sessionId), ("cwd", owner.Path), ("mcpServers", new JsonArray())), timeout.Token));
            using var finalStore = new Store(directory); finalStore.Delete(resumed); Assert.Empty(finalStore.Chats());
        }
    }
    private static async Task WaitForTurn(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(110);
        while (!window.FindControl<Button>("SendButton")!.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(window.FindControl<Button>("SendButton")!.IsEnabled, "Live ACP turn timed out");
    }
}
