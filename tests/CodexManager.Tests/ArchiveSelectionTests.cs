using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class ArchiveSelectionTests
{
    [AvaloniaFact]
    public async Task CheckboxesBulkUnarchiveAndDeleteOnlySelectedChats()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("archive-selection-").FullName);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var owner = new Workspace("w", "Archive", store.DirectoryPath); store.Save(owner);
        var chats = Enumerable.Range(0, 3).Select(i => new Chat { Id = "archived" + i, WorkspaceId = owner.Id, Title = "Saved chat " + i, Archived = true }).ToList();
        foreach (var chat in chats) store.Save(chat);
        var view = new MainView(store, [owner], chats);
        typeof(MainView).GetField("showArchived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(view, true);
        void Refresh() => typeof(MainView).GetMethod("BuildWorkspaceTree", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null);
        Refresh();
        var window = new Window { Content = view, Width = 1000, Height = 700 }; window.Show();
        T Named<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        try
        {
            window.UpdateLayout();
            Named<IconButton>("CollapseWorkspace_w").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Named<CheckBox>("SelectArchived_archived0").IsChecked = true;
            Named<CheckBox>("SelectArchived_archived1").IsChecked = true;
            Assert.Equal("2 selected", Named<TextBlock>("ArchiveSelectionCount").Text);
            Named<Button>("RestoreSelectedChats").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !chats[0].Archived && !chats[1].Archived);
            Assert.True(chats[2].Archived);
            chats[0].Archived = chats[1].Archived = true; Refresh(); window.UpdateLayout();
            Named<CheckBox>("SelectArchived_archived0").IsChecked = true;
            Named<CheckBox>("SelectArchived_archived1").IsChecked = true;
            Named<Button>("DeleteSelectedChats").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.GetVisualDescendants().OfType<Button>().Any(c => c.Name == "ConfirmBulkDelete"));
            window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ConfirmBulkDelete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => chats.Count == 1);
            Assert.Equal("archived2", chats[0].Id);
            Assert.Equal("archived2", Assert.Single(store.Chats()).Id);
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
    [AvaloniaFact]
    public async Task ArchiveBrowsingPreservesTheActiveChatAndDoubleClickRestoresAndOpens()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("archive-browse-").FullName);
        store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        var owner = new Workspace("w", "Open", store.DirectoryPath); var closed = new Workspace("closed", "Closed", store.DirectoryPath);
        store.Save(owner); store.Save(closed); store.Setting("closed:closed", "1");
        var active = new Chat { Id = "active", WorkspaceId = owner.Id };
        var older = new Chat { Id = "older", WorkspaceId = owner.Id, Archived = true, Updated = DateTimeOffset.UtcNow.AddDays(-1) };
        var newer = new Chat { Id = "newer", WorkspaceId = owner.Id, Archived = true };
        List<Chat> chats = [active, older, newer]; foreach (var chat in chats) store.Save(chat);
        var view = new MainView(store, [owner, closed], chats);
        var window = new Window { Content = view, Width = 1000, Height = 700 }; window.Show();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Mode(bool archived) => typeof(MainView).GetMethod("ShowArchiveView", flags)!.Invoke(view, [archived]);
        object? Current() => typeof(MainView).GetField("current", flags)!.GetValue(view);
        try
        {
            window.UpdateLayout(); Assert.Same(active, Current());
            Mode(true); window.UpdateLayout();
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Chats_w");
            Assert.False(list.IsVisible); Assert.Equal([newer, older], list.Items.Cast<Chat>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "Workspace_closed");
            Assert.Same(active, Current());
            view.GetVisualDescendants().OfType<IconButton>().Single(b => b.Name == "CollapseWorkspace_w").RaiseEvent(new(Button.ClickEvent));
            window.UpdateLayout(); Assert.True(list.IsVisible);
            list.SelectedItem = newer; Assert.Same(active, Current());
            Mode(false); window.UpdateLayout(); Assert.Same(active, Current());
            Mode(true); window.UpdateLayout();
            Assert.False(view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Chats_w").IsVisible);
            view.GetVisualDescendants().OfType<IconButton>().Single(b => b.Name == "CollapseWorkspace_w").RaiseEvent(new(Button.ClickEvent));
            window.UpdateLayout();
            var check = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "SelectArchived_newer");
            var row = Assert.IsType<Grid>(check.Parent);
            var content = Assert.IsType<Grid>(row.Children[1]);
            var title = content.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().First();
            var point = title.TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.True(newer.Archived); Assert.Same(active, Current());
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await WaitUntil(() => !newer.Archived && ReferenceEquals(newer, Current()));
            Assert.False(store.Chats().Single(c => c.Id == newer.Id).Archived);
            Assert.True(older.Archived);
            Assert.False((bool)typeof(MainView).GetField("showArchived", flags)!.GetValue(view)!);
        }
        finally { window.Close(); view.DisposeMobile(); }
    }
    [Theory]
    [InlineData("chat")]
    [InlineData("send")]
    [InlineData("reconnect")]
    public async Task RemoteArchivedChatsMustBeUnarchivedBeforeUse(string method)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("archive-gate-").FullName);
        var owner = new Workspace("w", "Archive", store.DirectoryPath); store.Save(owner);
        var chat = new Chat { WorkspaceId = owner.Id, Archived = true }; store.Save(chat);
        using var service = new SessionService(store, [owner], [chat], (_, _) => throw new Exception("Must not start archived agents"));
        var error = await Assert.ThrowsAsync<IOException>(() => service.Handle(new() { ["method"] = method, ["chatId"] = chat.Id }));
        Assert.Contains("Unarchive", error.Message);
        await service.Handle(new() { ["method"] = "archive", ["chatId"] = chat.Id, ["archived"] = false });
        Assert.False(chat.Archived);
    }
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    [Fact]
    public async Task RemoteCatalogOmitsClosedWorkspacesAndIncludesChatDates()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("archive-catalog-").FullName);
        var open = new Workspace("open", "Open", store.DirectoryPath); var closed = new Workspace("closed", "Closed", store.DirectoryPath);
        store.Save(open); store.Save(closed); store.Setting("closed:closed", "1");
        var visible = new Chat { WorkspaceId = open.Id, Archived = true }; var hidden = new Chat { WorkspaceId = closed.Id, Archived = true };
        using var service = new SessionService(store, [open, closed], [visible, hidden], (_, _) => throw new Exception("Catalog must not start agents"));
        var result = await service.Handle(new() { ["method"] = "list" });
        Assert.Equal("open", Assert.Single(result!["workspaces"]!.AsArray())!["id"]!.GetValue<string>());
        var chat = Assert.Single(result["chats"]!.AsArray())!;
        Assert.Equal(visible.Id, chat["id"]!.GetValue<string>());
        Assert.Equal(visible.Updated, DateTimeOffset.Parse(chat["updated"]!.GetValue<string>()));
    }
    [Fact]
    public async Task FailedProviderCleanupStillDeletesLocallyAndSuppressesReimport()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("delete-fallback-").FullName);
        var owner = new Workspace("w", "Delete", store.DirectoryPath); store.Save(owner);
        var chat = new Chat { WorkspaceId = owner.Id, Provider = AgentProvider.Dirac, SessionId = "saved-session", Archived = true }; store.Save(chat);
        var chats = new List<Chat> { chat }; var released = false;
        var warning = await ChatDeletion.Delete(store, chats, chat, owner, () => { released = true; return Task.CompletedTask; }, () => throw new IOException("provider offline"));
        Assert.True(released); Assert.Equal("provider offline", warning); Assert.Empty(chats); Assert.Empty(store.Chats());
        Assert.Equal("1", store.Setting(AgentProviders.HiddenHistoryKey(chat))); Assert.False(chat.Busy); Assert.False(chat.IsDeleting);
    }
    [Fact]
    public async Task RemoteDeletionUsesHostCleanupAndRejectsRunningChats()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("remote-delete-").FullName);
        var owner = new Workspace("w", "Delete", store.DirectoryPath); store.Save(owner);
        var chat = new Chat { WorkspaceId = owner.Id, Busy = true }; store.Save(chat); var chats = new List<Chat> { chat };
        using var service = new SessionService(store, [owner], chats, (_, _) => throw new Exception("Must not create runtime"))
        { DeleteChat = (c, w) => ChatDeletion.Delete(store, chats, c, w, () => Task.CompletedTask) };
        await Assert.ThrowsAsync<IOException>(() => service.Handle(new() { ["method"] = "delete", ["chatId"] = chat.Id }));
        Assert.Single(chats); chat.Busy = false;
        var result = await service.Handle(new() { ["method"] = "delete", ["chatId"] = chat.Id });
        Assert.True(result!["deleted"]!.GetValue<bool>()); Assert.Empty(chats); Assert.Empty(store.Chats());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderDeleteRequiresAdvertisedCapability(bool supported)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("acp-delete-").FullName);
        var owner = new Workspace("w", "Delete", store.DirectoryPath);
        var chat = new Chat { WorkspaceId = owner.Id, Provider = AgentProvider.Dirac, SessionId = "target-session" };
        var script = "const emit=m=>console.log(JSON.stringify(m));require('readline').createInterface({input:process.stdin}).on('line',s=>{const m=JSON.parse(s);if(m.method==='initialize')emit({jsonrpc:'2.0',id:m.id,result:{agentCapabilities:{sessionCapabilities:" + (supported ? "{delete:{}}" : "{}") + "}}});else if(m.method==='session/delete'&&m.params.sessionId==='target-session')emit({jsonrpc:'2.0',id:m.id,result:{}});else process.exit(9);});";
        var path = Path.Combine(store.DirectoryPath, "delete.cjs"); await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);
        store.Setting(AgentProviders.CommandKey(chat.Provider, false), "node \"" + path + "\"");
        var warning = await ChatHistory.TryDeleteFromProvider(store, owner, chat);
        if (supported) Assert.Null(warning); else Assert.Contains("does not expose", warning);
    }
}
