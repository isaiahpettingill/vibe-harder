namespace CodexManager.Tests;

public class WorkspaceHistoryTests
{
    [Fact]
    public async Task HistoryRemovalPreservesChatsAndReopeningRestoresEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-history", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var existing = new Workspace("existing", "Existing", directory);
        var deleted = new Workspace("deleted", "Deleted", Path.Combine(directory, "gone"));
        store.Save(existing); store.Save(deleted);
        store.Save(new Chat { WorkspaceId = existing.Id, Provider = AgentProvider.Claude });
        var history = new WorkspaceHistory(store);
        history.Opened(existing);
        Assert.Equal(existing, history.Entries()[0]);
        history.Remove(existing);
        Assert.DoesNotContain(existing, new WorkspaceHistory(store).Entries());
        Assert.Single(store.Chats());
        Assert.True(await WorkspaceHistory.Exists(existing));
        Assert.False(await WorkspaceHistory.Exists(deleted));
        history.Remove(deleted); history.Opened(existing);
        Assert.Equal(existing, Assert.Single(history.Entries()));
    }

    [Fact]
    public async Task UninstalledDistroIsDeletedButADisconnectedDriveIsUnknown()
    {
        if (OperatingSystem.IsWindows())
        {
            // Only meaningful when WSL can list distros; an uninstalled one takes its folders with it.
            string[]? distros = null;
            try { distros = await Hosts.Distros(); } catch (IOException) { }
            if (distros is not null) Assert.False(await WorkspaceHistory.Exists(new Workspace("w", "Gone", "/home/me/repo", "NoSuchDistro-" + Guid.NewGuid().ToString("N")[..8])));
            var unused = "DEFGHIJKLMNOPQRSTUVWXYZ".First(letter => !Directory.Exists(letter + ":\\"));
            Assert.Null(await WorkspaceHistory.Exists(new Workspace("d", "Unplugged", unused + ":\\projects\\app")));
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task StartupClosesWorkspacesWhoseFolderWasDeletedAndKeepsTheirChats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-archive-missing", Guid.NewGuid().ToString("N"));
        var gone = Path.Combine(directory, "deleted-project"); Directory.CreateDirectory(gone);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        string chatId;
        using (var store = new Store(directory))
        {
            store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
            store.Save(new Workspace("kept", "Kept", directory));
            store.Save(new Workspace("gone", "Deleted project", gone));
            var chat = new Chat { WorkspaceId = "gone", Title = "Old work" }; store.Save(chat); chatId = chat.Id;
        }
        Directory.Delete(gone);
        var window = new MainWindow(); window.Show();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (UiTests.Named<Avalonia.Controls.StackPanel>(window, "WorkspaceTree").Children.Count != 1 && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Single(UiTests.Named<Avalonia.Controls.StackPanel>(window, "WorkspaceTree").Children);
        }
        finally { window.Close(); await Task.Delay(300, TestContext.Current.CancellationToken); }
        using var saved = new Store(directory);
        Assert.Equal("1", saved.Setting("closed:gone"));
        Assert.Equal("1", saved.Setting("historyRemoved:gone"));
        Assert.Equal("0", saved.Setting("closed:kept") ?? "0");
        Assert.Contains(saved.Chats(), c => c.Id == chatId);
    }
}
