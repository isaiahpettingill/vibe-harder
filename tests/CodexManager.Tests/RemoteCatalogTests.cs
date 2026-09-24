using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class RemoteCatalogTests
{
    [Theory]
    [InlineData(@"C:\repos\my_project-name\", "my_project-name")]
    [InlineData("/home/user/my_project-name/", "my_project-name")]
    [InlineData("/", "/")]
    public void DefaultWorkspaceNameKeepsFolderPunctuation(string path, string expected) => Assert.Equal(expected, Workspace.DefaultName(path));

    [Fact]
    public async Task RenameWorkspaceUpdatesCatalogAndSavedWorkspace()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("rename-workspace-").FullName);
        var owner = new Workspace("workspace", "Initial", store.DirectoryPath);
        store.Save(owner);
        var workspaces = new List<Workspace> { owner };
        using var service = new SessionService(store, workspaces, [], (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<IOException>(() => service.Handle(new() { ["method"] = "workspace/rename", ["workspaceId"] = owner.Id, ["name"] = "  " }));
        await service.Handle(new() { ["method"] = "workspace/rename", ["workspaceId"] = owner.Id, ["name"] = "  My_project-name  " });
        Assert.Equal("My_project-name", Assert.Single(workspaces).Name);
        Assert.Equal("My_project-name", Assert.Single(store.Workspaces()).Name);
        Assert.Equal("My_project-name", (await service.Handle(new() { ["method"] = "list" }))!["workspaces"]!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.Equal(owner.Path, Assert.Single(workspaces).Path);
    }

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
