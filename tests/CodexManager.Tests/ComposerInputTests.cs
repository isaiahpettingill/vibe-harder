using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    private static void Key(Control input, Key key, KeyModifiers modifiers = KeyModifiers.None) => input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
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
            var input = window.FindControl<ComposerEditor>("Composer")!; var attachments = window.FindControl<ItemsControl>("AttachmentList")!;
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
    public async Task LongPasteBecomesTextAttachmentAndLargeDraftsStayEditable()
    {
        var directory = Directory.CreateTempSubdirectory("composer-long-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Test", directory)); store.Save(new Chat { WorkspaceId = "w" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + System.IO.Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var window = new MainWindow(store); window.Show();
        try
        {
            var input = window.FindControl<ComposerEditor>("Composer")!; var attachments = window.FindControl<ItemsControl>("AttachmentList")!;
            await window.Clipboard!.SetTextAsync("short text"); input.Text = "a "; Key(input, Avalonia.Input.Key.V, KeyModifiers.Control);
            await Wait(() => input.Text == "a short text"); Assert.Equal(0, attachments.ItemCount);
            var longText = string.Join('\n', Enumerable.Range(0, 400).Select(i => "pasted line " + i));
            await window.Clipboard!.SetTextAsync(longText); input.Text = "see "; Key(input, Avalonia.Input.Key.V, KeyModifiers.Control);
            await Wait(() => input.Text == "see [Pasted text #1]");
            var pasted = Assert.IsType<Attachment>(Assert.Single(attachments.Items));
            Assert.Equal(("Pasted text 1.txt", "text/plain", longText, "[Pasted text #1]"), (pasted.Name, pasted.MimeType, pasted.Data, pasted.Reference));
            Assert.Equal(longText, pasted.ToContent()["resource"]!["text"]!.GetValue<string>());
            await window.Clipboard!.SetTextAsync(longText + "!"); Key(input, Avalonia.Input.Key.Insert, KeyModifiers.Shift);
            await Wait(() => input.Text == "see [Pasted text #1][Pasted text #2]");
            window.UpdateLayout(); Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<IconButton>().First(b => ReferenceEquals(b.Tag, pasted)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("see [Pasted text #2]", input.Text); Assert.Equal(1, attachments.ItemCount);

            // A huge typed draft only lays out visible lines, so edits and relayout stay fast.
            var draft = string.Join('\n', Enumerable.Range(0, 50_000).Select(i => "typed line " + i));
            input.Text = draft; window.UpdateLayout();
            Assert.Equal(draft.Length, input.CaretIndex); Assert.True(input.Bounds.Height <= 190);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 50; i++) { input.SelectedText = "x"; window.UpdateLayout(); }
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), timer.Elapsed.ToString());
            Assert.Equal(draft + new string('x', 50), input.Text); Assert.Equal(input.Text, Assert.IsType<Chat>(UiTests.Named<ListBox>(window, "Chats_w").SelectedItem).Draft);
        }
        finally { window.RequestExit(); await Wait(() => !window.IsVisible); }
    }
}
