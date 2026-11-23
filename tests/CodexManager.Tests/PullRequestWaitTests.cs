using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public sealed class PullRequestWaitTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [Trait("Category", "CI")]
    [InlineData("OPEN", "[]", false, false)]
    [InlineData("OPEN", "[{\"status\":\"COMPLETED\",\"conclusion\":\"SUCCESS\"},{\"state\":\"PENDING\"}]", false, false)]
    [InlineData("OPEN", "[{\"status\":\"COMPLETED\",\"conclusion\":\"SUCCESS\"},{\"state\":\"SUCCESS\"}]", true, true)]
    [InlineData("OPEN", "[{\"status\":\"COMPLETED\",\"conclusion\":\"FAILURE\"}]", true, false)]
    [InlineData("MERGED", "[{\"state\":\"SUCCESS\"}]", false, false)]
    [InlineData("CLOSED", "[{\"state\":\"SUCCESS\"}]", false, false)]
    public async Task OnlyRegisteredChatResumesOnceAfterChecksFinish(string state, string checks, bool resumes, bool passes)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("pr-wait-").FullName);
        var workspace = new Workspace("workspace", "", store.DirectoryPath);
        var requester = new Chat { WorkspaceId = workspace.Id };
        var other = new Chat { WorkspaceId = workspace.Id };
        store.Save(workspace); store.Save(requester); store.Save(other);
        using var service = new SessionService(store, [workspace], [requester, other], (_, _) => throw new InvalidOperationException());
        await service.HandleLocal(new JsonObject { ["method"] = "pr/wait", ["chatId"] = requester.Id, ["repository"] = "Org/Repo", ["number"] = "4" });
        using var reopened = new Store(store.DirectoryPath);
        var chats = reopened.Chats();
        var chat = chats.Single(c => c.Id == requester.Id);
        var waits = new PullRequestWaits(reopened, [workspace], chats, (_, _) => throw new InvalidOperationException());
        var reads = 0;
        Task<JsonObject?> Read(Workspace _, string url)
        {
            Assert.Equal("https://github.com/org/repo/pull/4", url);
            reads++;
            return Task.FromResult<JsonObject?>(new() { ["state"] = state, ["headRefOid"] = Head, ["statusCheckRollup"] = JsonNode.Parse(checks) });
        }
        var prompts = new List<PendingInput>();
        Task Send(Chat owner, Workspace _, PendingInput input)
        {
            Assert.Same(chat, owner);
            Assert.Contains(input, owner.QueuedInputs);
            owner.QueuedInputs.Remove(input);
            prompts.Add(input);
            return Task.CompletedTask;
        }
        var token = TestContext.Current.CancellationToken;
        chat.Busy = true;
        await waits.Refresh(token, Read, Send);
        Assert.Equal(0, reads);
        chat.Busy = false;
        await waits.Refresh(token, Read, Send);
        await waits.Refresh(token, Read, Send);
        Assert.Equal(resumes || state != "OPEN" ? 1 : 2, reads);
        if (resumes)
        {
            var prompt = Assert.Single(prompts);
            Assert.Contains(Head, prompt.Text);
            Assert.Contains(passes ? "(passed)" : "(not all passed)", prompt.Text);
        }
        else Assert.Empty(prompts);
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task CompletedChecksStartAnotherTurnInTheSameAgentSession()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("pr-wait-session-").FullName);
        var workspace = new Workspace("workspace", "", store.DirectoryPath);
        var chat = new Chat { WorkspaceId = workspace.Id };
        store.Save(workspace); store.Save(chat);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + fixture + "\"");
        await runtime.Send("Original request", []);
        Assert.False(string.IsNullOrEmpty(chat.SessionId));
        var session = chat.SessionId;
        var waits = new PullRequestWaits(store, [workspace], [chat], (_, _) => runtime);
        waits.Set(chat.Id, "org/repo", "4", false);
        var token = TestContext.Current.CancellationToken;
        Task<JsonObject?> Read(Workspace _, string url) => Task.FromResult<JsonObject?>(new()
        {
            ["state"] = "OPEN", ["headRefOid"] = Head, ["statusCheckRollup"] = JsonNode.Parse("[{\"state\":\"SUCCESS\"}]")
        });
        await waits.Refresh(token, Read);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (chat.Busy && DateTime.UtcNow < until) await Task.Delay(20, token);
        Assert.False(chat.Busy);
        Assert.Equal(session, chat.SessionId);
        Assert.Single(chat.Messages, m => m.Role == "user" && m.Text.Contains(Head, StringComparison.Ordinal));
        await waits.Refresh(token, Read);
        Assert.False(chat.Busy);
    }

    [Theory]
    [Trait("Category", "CI")]
    [InlineData("org/repo;whoami", "4")]
    [InlineData("org/repo", "4 --help")]
    [InlineData("org/repo", "0")]
    public void RejectsInvalidPullRequestReferences(string repo, string number)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("pr-wait-invalid-").FullName);
        var chat = new Chat { WorkspaceId = "workspace" };
        var waits = new PullRequestWaits(store, [], [chat], (_, _) => throw new InvalidOperationException());
        Assert.Throws<IOException>(() => waits.Set(chat.Id, repo, number, false));
    }
}
