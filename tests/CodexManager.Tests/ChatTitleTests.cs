using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class ChatTitleTests
{
    [AvaloniaFact]
    public async Task AgentNamedSessionsReplaceTheFirstLineUnlessTheUserRenamedTheChat()
    {
        var directory = Directory.CreateTempSubdirectory("chat-titles-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Titles", directory); store.Save(workspace);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
        var command = OperatingSystem.IsWindows() ? $"node '{fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(fixture)}";

        var chat = new Chat { WorkspaceId = "w", Title = "named" }; store.Save(chat);
        await using (var runtime = new ChatRuntime(chat, workspace, store, command)) await runtime.Send("named", []);
        Assert.Equal("Fix the flaky login test", chat.Title);
        Assert.Equal("Fix the flaky login test", store.Chats().Single(c => c.Id == chat.Id).Title);

        var renamed = new Chat { WorkspaceId = "w", Title = "My own title" }; store.Save(renamed); ChatTitles.MarkRenamed(store, renamed);
        await using (var runtime = new ChatRuntime(renamed, workspace, store, command)) await runtime.Send("named", []);
        Assert.Equal("My own title", renamed.Title);
    }
}
