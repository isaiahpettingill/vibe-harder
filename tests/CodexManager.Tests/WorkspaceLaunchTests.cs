using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class WorkspaceLaunchTests
{
    [Fact]
    public void ArgumentsPreserveStartupAndUpdaterAndNormalizeFolders()
    {
        Assert.Null(WorkspaceLaunch.Parse([]));
        Assert.Null(WorkspaceLaunch.Parse(["--startup"]));
        Assert.Null(WorkspaceLaunch.Parse(["--updated"]));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), WorkspaceLaunch.Parse(["."]));
        Assert.Equal(WorkspaceLaunch.Parse(["."]), WorkspaceLaunch.Parse(["--workspace", "."]));
        Assert.Throws<ArgumentException>(() => WorkspaceLaunch.Parse(["--workspace"]));
        Assert.Throws<DirectoryNotFoundException>(() => WorkspaceLaunch.Parse([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]));
    }

    [AvaloniaFact]
    public async Task DirectoryLaunchReopensExistingLocalWorkspaceWithoutDuplicatingIt()
    {
        var directory = Directory.CreateTempSubdirectory("workspace-launch-").FullName;
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Folder with spaces")).FullName;
        var window = new MainWindow(store); window.Show();
        store.Save(new Workspace("wsl", "WSL", folder, "Debian"));
        try
        {
            window.View.OpenLocalDirectory(folder);
            var local = Assert.Single(store.Workspaces(), w => !w.IsWsl);
            Assert.Equal(folder, local.Path); Assert.Equal(local.Id, store.Setting("workspace"));
            store.Setting("closed:" + local.Id, "1");
            window.View.OpenLocalDirectory(Path.Combine(folder, ".") + Path.DirectorySeparatorChar);
            Assert.Equal(local.Id, Assert.Single(store.Workspaces(), w => !w.IsWsl).Id);
            Assert.Equal("0", store.Setting("closed:" + local.Id));
            Assert.Contains(window.GetLogicalDescendants().OfType<ListBox>(), list => list.Name == "Chats_" + local.Id);
        }
        finally
        {
            window.RequestExit();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (window.IsVisible && DateTime.UtcNow < until) await Task.Delay(25);
            Assert.False(window.IsVisible);
        }
    }
}
