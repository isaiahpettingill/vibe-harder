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
}
