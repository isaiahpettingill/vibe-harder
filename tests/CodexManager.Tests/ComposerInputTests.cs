using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace CodexManager.Tests;

public class ComposerInputTests
{
    private static async Task Wait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }
    private static void Key(TextBox input, Key key, KeyModifiers modifiers = KeyModifiers.None) => input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    private static byte[] Png()
    {
        using var image = new RenderTargetBitmap(new PixelSize(8, 8));
        using (var drawing = image.CreateDrawingContext()) drawing.FillRectangle(Avalonia.Media.Brushes.Coral, new Rect(0, 0, 8, 8));
        using var bytes = new MemoryStream(); image.Save(bytes, PngBitmapEncoderOptions.Default); return bytes.ToArray();
    }
    private static DataTransfer ImageData(byte[] bytes)
    {
        var data = new DataTransfer(); var item = new DataTransferItem();
        item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), bytes); data.Add(item); return data;
    }
    public class PortalFile : System.Reflection.DispatchProxy
    {
        private byte[] bytes = [];
        public static IStorageFile Create(byte[] bytes)
        {
            var file = Create<IStorageFile, PortalFile>(); ((PortalFile)(object)file).bytes = bytes; return file;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Name" => "portal-image.png",
            "get_Path" => new Uri("content://portal/image"),
            "get_CanBookmark" => false,
            "OpenReadAsync" => Task.FromResult<Stream>(new MemoryStream(bytes)),
            "Dispose" => null,
            _ => throw new NotSupportedException(method?.Name)
        };
    }
    [AvaloniaFact]
    public async Task LocalComposerAcceptsMimeClipboardAndPortalDropThroughTextBox()
    {
        var directory = Directory.CreateTempSubdirectory("composer-local-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Test", directory)); store.Save(new Chat { WorkspaceId = "w" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + System.IO.Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var window = new MainWindow(store); window.Show();
        try
        {
            var input = window.FindControl<TextBox>("Composer")!; var attachments = window.FindControl<ItemsControl>("AttachmentList")!;
            var bytes = Png(); await window.Clipboard!.SetDataAsync(ImageData(bytes)); Key(input, Avalonia.Input.Key.V, KeyModifiers.Control);
            await Wait(() => attachments.ItemCount == 1);
            var drop = new DataTransfer(); drop.Add(DataTransferItem.CreateFile(PortalFile.Create(bytes)));
            input.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, drop, input, default, KeyModifiers.None));
            await Wait(() => attachments.ItemCount == 2);
            Assert.All(attachments.Items.OfType<Attachment>(), a => Assert.NotNull(a.Thumbnail));
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }
    [AvaloniaFact]
    public async Task RemoteComposerCompletesCommandsAndSupportsEnterPasteAndDrop()
    {
        var directory = Directory.CreateTempSubdirectory("composer-remote-").FullName;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var requests = new List<string>();
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request =>
        {
            var method = request["method"]!.GetValue<string>(); requests.Add(method);
            return Task.FromResult<JsonNode?>(method == "list"
                ? new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "w", ["name"] = "Test" }), ["chats"] = new JsonArray(new JsonObject { ["id"] = "c", ["workspaceId"] = "w", ["title"] = "Chat", ["archived"] = false }) }
                : method == "chat" ? new JsonObject { ["busy"] = false, ["status"] = "Ready", ["queued"] = 1, ["config"] = new JsonArray(), ["messages"] = new JsonArray(), ["permissions"] = new JsonArray(), ["queue"] = new JsonArray(), ["commands"] = new JsonArray("/compact", "/goal") } : new JsonObject { ["busy"] = false, ["preparing"] = false });
        });
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), System.IO.Path.Combine(directory, "key"), "Test", TestContext.Current.CancellationToken);
        using var view = new RemoteView(host); var window = new Window { Content = view }; window.Show();
        try
        {
            await Wait(() => requests.Contains("chat"));
            var input = view.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "RemoteComposer");
            input.Text = "/co"; await Wait(() => view.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            Key(input, Avalonia.Input.Key.Escape); Assert.Equal("/co", input.Text);
            Assert.False(view.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            input.Text = "/com"; await Task.Delay(50); Key(input, Avalonia.Input.Key.Tab); Assert.Equal("/compact ", input.Text);
            input.CaretIndex = input.Text.Length; Key(input, Avalonia.Input.Key.Enter, KeyModifiers.Control); Assert.EndsWith("\n", input.Text);
            input.Text = "hello"; await Task.Delay(50); Key(input, Avalonia.Input.Key.Enter); await Wait(() => requests.Contains("send"));
            await Wait(() => input.Text == ""); Key(input, Avalonia.Input.Key.Enter); await Wait(() => requests.Contains("queue/advance"));
            var bytes = Png(); await window.Clipboard!.SetDataAsync(ImageData(bytes)); Key(input, Avalonia.Input.Key.V, KeyModifiers.Control);
            await Task.Delay(100);
            var drop = new DataTransfer(); drop.Add(DataTransferItem.CreateFile(PortalFile.Create(bytes)));
            input.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, drop, input, default, KeyModifiers.None));
            await Task.Delay(100);
            var attachments = (List<Attachment>)typeof(RemoteView).GetField("attachments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
            Assert.Equal(2, attachments.Count); Assert.All(attachments, a => Assert.NotNull(a.Thumbnail));
        }
        finally { window.Close(); }
    }
}
