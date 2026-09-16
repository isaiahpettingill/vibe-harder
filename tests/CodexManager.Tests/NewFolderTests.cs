using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class NewFolderTests
{
    [Fact]
    public async Task HostCreatesOneFolderAndRejectsTraversalAndDuplicates()
    {
        var parent = Directory.CreateTempSubdirectory("new-workspace-").FullName;
        var created = await Hosts.CreateDirectory(null, parent, "Project with spaces");
        Assert.Equal(Path.Combine(parent, "Project with spaces"), created); Assert.True(Directory.Exists(created));
        await Assert.ThrowsAsync<IOException>(() => Hosts.CreateDirectory(null, parent, "Project with spaces"));
        foreach (var name in new[] { "", "..", "../outside", "a/b", "a\\b" })
            await Assert.ThrowsAsync<ArgumentException>(() => Hosts.CreateDirectory(null, parent, name));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Hosts.CreateDirectory(null, Path.Combine(parent, "missing"), "child"));
    }

    [AvaloniaFact]
    public async Task RemotePickerCreatesInDisplayedFolderAndNavigatesIntoIt()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("new-remote-folder-").FullName);
        using var service = new SessionService(store, [], [], (_, _) => throw new Exception("Must not start an agent"));
        var picker = new RemoteWorkspacePicker(request =>
        {
            if (request["method"]!.GetValue<string>() == "locations") return Task.FromResult<System.Text.Json.Nodes.JsonNode?>(new System.Text.Json.Nodes.JsonObject { ["distros"] = new System.Text.Json.Nodes.JsonArray() });
            if (request["method"]!.GetValue<string>() == "directories" && request["path"]!.GetValue<string>() == "") request["path"] = store.DirectoryPath;
            return service.Handle(request);
        });
        var window = new Window { Content = picker, Width = 390, Height = 650 }; window.Show();
        T Find<T>(string name) where T : Control => picker.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var button = Find<NewFolderButton>("NewFolderButton");
            while (!button.IsEnabled) await Task.Delay(20, timeout.Token);
            button.Flyout!.ShowAt(button);
            var content = (StackPanel)((Flyout)button.Flyout).Content!;
            content.GetLogicalDescendants().OfType<TextBox>().Single().Text = "My project";
            content.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "CreateFolderButton").RaiseEvent(new(Button.ClickEvent));
            var destination = Path.Combine(store.DirectoryPath, "My project");
            while (Find<TextBox>("RemoteFolderPath").Text != destination) await Task.Delay(20, timeout.Token);
            Assert.True(Directory.Exists(destination)); Assert.Null(picker.OpenedWorkspaceId);
        }
        finally { window.Close(); }
    }
}
