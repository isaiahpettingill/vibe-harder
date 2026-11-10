using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class RemoteCatalogTests
{
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(null)]
    [InlineData("provider-session-123")]
    public async Task CatalogIncludesProviderSessionIdForFailedAndArchivedChats(string? sessionId)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("catalog-session-").FullName);
        var owner = new Workspace("workspace", "Project", store.DirectoryPath);
        var chat = new Chat { Id = "local-chat", WorkspaceId = owner.Id, SessionId = sessionId, Archived = true, Status = "Failed" };
        using var service = new SessionService(store, [owner], [chat], (_, _) => throw new InvalidOperationException());
        var catalog = await service.Handle(new() { ["method"] = "list" });
        var record = Assert.Single(catalog!["chats"]!.AsArray());
        Assert.Equal(chat.Id, record!["id"]!.GetValue<string>());
        Assert.Equal(sessionId, record["sessionId"]?.GetValue<string>());
        var remoteChat = new Chat { Id = "remote-local-chat", WorkspaceId = owner.Id };
        RemoteChatSession.ApplySummary(remoteChat, record);
        Assert.Equal(sessionId, remoteChat.SessionId);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("{\"used\":4200}", "", "4.2K context tokens")]
    [InlineData("{\"used\":900,\"totalTokens\":1255515,\"inputTokens\":1200000,\"outputTokens\":55515}", "", "1.3M tokens")]
    [InlineData("{\"used\":1200,\"totals\":{\"USD\":0.42}}", "USD 0.42", "1.2K context tokens")]
    [InlineData("{}", "", "")]
    public async Task CatalogReportsHostUsageToPairedDevices(string usage, string expectedCost, string expectedContext)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("catalog-usage-").FullName);
        var owner = new Workspace("workspace", "Project", store.DirectoryPath);
        var chat = new Chat { Id = "chat", WorkspaceId = owner.Id };
        store.Setting("usage:" + chat.Id, usage);
        using var service = new SessionService(store, [owner], [chat], (_, _) => throw new InvalidOperationException());
        var record = Assert.Single((await service.Handle(new() { ["method"] = "list" }))!["chats"]!.AsArray());
        Assert.Equal(expectedCost, record!["cost"]?.GetValue<string>());
        Assert.Equal(expectedContext, record["context"]?.GetValue<string>());
        var paired = new Chat { Id = "paired", WorkspaceId = owner.Id };
        RemoteChatSession.ApplySummary(paired, record);
        Assert.Equal(expectedCost, paired.CostLabel);
        Assert.Equal(expectedContext, paired.ContextLabel);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("  My_project-name  ", "My_project-name")]
    [InlineData("Project with spaces", "Project with spaces")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public async Task RenameWorkspaceUpdatesCatalogAndSavedWorkspace(string? requested, string? expected)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("rename-workspace-").FullName);
        var owner = new Workspace("workspace", "Initial", store.DirectoryPath);
        store.Save(owner);
        var workspaces = new List<Workspace> { owner };
        using var service = new SessionService(store, workspaces, [], (_, _) => throw new InvalidOperationException());
        var rename = service.Handle(new() { ["method"] = "workspace/rename", ["workspaceId"] = owner.Id, ["name"] = requested });
        if (expected is null) await Assert.ThrowsAsync<IOException>(() => rename); else await rename;
        var name = expected ?? owner.Name;
        Assert.Equal(name, Assert.Single(workspaces).Name);
        Assert.Equal(name, Assert.Single(store.Workspaces()).Name);
        Assert.Equal(name, (await service.Handle(new() { ["method"] = "list" }))!["workspaces"]!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.Equal(owner.Path, Assert.Single(workspaces).Path);
    }

    [Trait("Category", "CI")]
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
