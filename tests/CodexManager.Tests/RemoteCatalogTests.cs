using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class RemoteCatalogTests
{
    [Fact]
    public async Task CatalogUsesHostSidebarOrderAndWorkspaceVisibility()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("remote-catalog-").FullName);
        var first = new Workspace("first", "First", store.DirectoryPath);
        var second = new Workspace("second", "Second", store.DirectoryPath);
        store.Save(first); store.Save(second);
        var older = new Chat { Id = "older", WorkspaceId = first.Id, Title = "Older" };
        var newer = new Chat { Id = "newer", WorkspaceId = first.Id, Title = "Newer" };
        store.Save(older); store.Save(newer);
        using var service = new SessionService(store, [first, second], [older, newer], (_, _) => throw new InvalidOperationException());
        await service.Handle(new JsonObject { ["method"] = "list" });
        await service.Handle(new JsonObject { ["method"] = "reorder", ["scope"] = "workspaces", ["source"] = second.Id, ["target"] = first.Id });
        await service.Handle(new JsonObject { ["method"] = "reorder", ["scope"] = "chats:" + first.Id, ["source"] = newer.Id, ["target"] = older.Id });
        var catalog = await service.Handle(new JsonObject { ["method"] = "list" });
        Assert.Equal([second.Id, first.Id], catalog!["workspaces"]!.AsArray().Select(w => w!["id"]!.GetValue<string>()));
        Assert.Equal([newer.Id, older.Id], catalog["chats"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()));
        await service.Handle(new JsonObject { ["method"] = "workspace/close", ["workspaceId"] = first.Id });
        catalog = await service.Handle(new JsonObject { ["method"] = "list" });
        Assert.Equal([second.Id], catalog!["workspaces"]!.AsArray().Select(w => w!["id"]!.GetValue<string>()));
        Assert.Empty(catalog["chats"]!.AsArray());
    }
}
