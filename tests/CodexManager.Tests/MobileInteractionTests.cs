using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class MobileInteractionTests
{
    [Trait("Category", "CI")]
    [Fact]
    public async Task AttachmentsPreserveTextImagesAndBinaryFiles()
    {
        foreach (var (name, bytes, key) in new[] { ("notes.txt", Encoding.UTF8.GetBytes("hello 🌎"), "text"), ("photo.png", new byte[] { 137, 80, 78, 71 }, "data"), ("report.pdf", new byte[] { 37, 80, 68, 70, 0, 255 }, "blob") })
        {
            var attachment = await AttachmentFiles.Read(name, new MemoryStream(bytes), TestContext.Current.CancellationToken);
            var content = attachment.ToContent();
            var value = (key == "data" ? content[key] : content["resource"]![key])!.GetValue<string>();
            Assert.Equal(bytes, key == "text" ? Encoding.UTF8.GetBytes(value) : Convert.FromBase64String(value));
        }
        await Assert.ThrowsAsync<IOException>(() => AttachmentFiles.Read("large.txt", new MemoryStream(new byte[AttachmentFiles.MaximumBytes + 1]), TestContext.Current.CancellationToken));
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task NewSessionsDefaultToFullAccessAndCanBeChangedAfterwards()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var store = new Store(directory); var workspace = new Workspace("w", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config --access --reset-access");
        await runtime.Connect();
        Assert.Equal("full-access", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.Reconnect();
        Assert.Equal("full-access", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "model"), "large");
        Assert.Equal("full-access", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "mode"), "ask");
        Assert.Equal("ask", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.Reconnect();
        Assert.Equal("ask", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public async Task RemoteFolderPickerBrowsesHostAndOpensSelectedChild()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var child = Directory.CreateDirectory(Path.Combine(directory, "my_project-name")).FullName;
        using var store = new Store(directory); var workspaces = new List<Workspace>();
        var service = new SessionService(store, workspaces, [], (_, _) => throw new InvalidOperationException());
        var picker = new RemoteWorkspacePicker(service.Handle); var closed = false; picker.Closed += () => closed = true;
        var window = new Window { Width = 390, Height = 700, Content = picker }; window.Show();
        T Field<T>(string name) where T : Control => picker.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        async Task Wait(Func<bool> ready) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!ready()) await Task.Delay(20, timeout.Token); }
        try
        {
            await Wait(() => Field<ComboBox>("RemoteLocation").SelectedItem is not null);
            // Navigate through the same host API used by the browser, then type a known absolute folder.
            var listing = await service.Handle(new() { ["method"] = "directories", ["path"] = directory });
            Assert.Contains(child, listing!["directories"]!.AsArray().Select(d => d!.GetValue<string>()));
            Field<TextBox>("RemoteFolderPath").Text = child;
            Field<Button>("OpenRemoteFolder").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => closed); Assert.Equal(child, Assert.Single(workspaces).Path);
            Assert.Equal("my_project-name", Assert.Single(workspaces).Name);
            await service.Handle(new() { ["method"] = "workspace", ["path"] = child, ["name"] = "Again" });
            Assert.Single(workspaces);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.Handle(new() { ["method"] = "workspace", ["path"] = Path.Combine(directory, "missing") }));
        }
        finally { window.Close(); }
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void KeyboardInsetsReserveSpaceAndRestoreItWhenClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var view = new MainView(new Store(directory), remoteOnly: true);
        var handler = typeof(MainView).GetMethod("InputPaneChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        handler.Invoke(view, [null, new InputPaneStateEventArgs(InputPaneState.Open, null, new Rect(0, 400, 390, 280), TimeSpan.Zero, null)]);
        Assert.Equal(280, view.FindControl<Grid>("RootPanes")!.Margin.Bottom);
        handler.Invoke(view, [null, new InputPaneStateEventArgs(InputPaneState.Closed, null, default, TimeSpan.Zero, null)]);
        Assert.Equal(0, view.FindControl<Grid>("RootPanes")!.Margin.Bottom); view.DisposeMobile();
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void NativeInsetsClearStaleKeyboardSpaceAndDoNotDoubleCountResize()
    {
        var view = new MainView(new Store(Directory.CreateTempSubdirectory("inset-test-").FullName), remoteOnly: true);
        var root = view.FindControl<Grid>("RootPanes")!;
        try
        {
            view.UpdateNativeMobileInsets(new Thickness(0, 24, 0, 24), 280);
            Assert.Equal(280, root.Margin.Bottom);
            view.UpdateNativeMobileInsets(new Thickness(0, 24, 0, 24), 0);
            Assert.Equal(24, root.Margin.Bottom);
            var handler = typeof(MainView).GetMethod("InputPaneChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            handler.Invoke(view, [null, new InputPaneStateEventArgs(InputPaneState.Open, null, new Rect(0, 400, 390, 280), TimeSpan.Zero, null)]);
            Assert.Equal(24, root.Margin.Bottom);
            view.UpdateNativeMobileInsets(new Thickness(0, 24, 0, 0), 0); // Android already resized the viewport.
            Assert.Equal(0, root.Margin.Bottom);
        }
        finally { view.DisposeMobile(); }
    }
}
