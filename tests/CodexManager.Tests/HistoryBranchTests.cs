using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class HistoryBranchTests
{
    private static (Store Store, Workspace Workspace, Chat Chat, string Command) Setup(string flags = "")
    {
        var directory = Directory.CreateTempSubdirectory("history-branch-").FullName;
        var file = Path.Combine(directory, "provider.json");
        File.WriteAllText(file, """{"original":[{"id":"u1","role":"user","text":"First question"},{"id":"a1","role":"assistant","text":"First answer"},{"id":"u2","role":"user","text":"Second question"},{"id":"a2","role":"assistant","text":"Second answer"}]}""");
        var store = new Store(directory); var workspace = new Workspace("workspace", "Test", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, SessionId = "original", Provider = AgentProvider.Claude }; store.Save(chat);
        return (store, workspace, chat, "node \"" + Path.Combine(AppContext.BaseDirectory, "history-acp.mjs") + "\" \"" + file + "\" " + flags);
    }
    [AvaloniaFact]
    public async Task EditingReplacesCurrentHistoryAndRetainsTheOriginalProviderBranch()
    {
        var (store, workspace, chat, command) = Setup(); using var dispose = store;
        await using var runtime = new ChatRuntime(chat, workspace, store, command); await runtime.LoadHistory();
        var selected = chat.Messages.Single(m => m.ProviderMessageId == "u2");
        var options = await runtime.HistoryOptions(selected.Id, selected.Sequence); Assert.True(options["edit"]!.GetValue<bool>());
        var result = await runtime.BranchHistory(selected.Id, selected.Sequence, options["checkpoint"]!.GetValue<string>(), false);
        Assert.Same(chat, result); Assert.NotEqual("original", chat.SessionId);
        Assert.Equal(new[] { "First question", "First answer" }, (await store.ReadPageAsync(chat)).Select(m => m.Text));
        await runtime.Send("Rewritten question", []);
        Assert.Contains(chat.Messages, m => m.Text == "Rewritten question");
        Assert.DoesNotContain(chat.Messages, m => m.Text == "Second answer");
        Assert.Single(store.Chats());
        Assert.Equal(4, JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.Path, "provider.json")))!["original"]!.AsArray().Count);
    }
    [AvaloniaFact]
    public async Task ForkAtOutputCreatesIndependentChatAndPersistsMessageIds()
    {
        var (store, workspace, chat, command) = Setup(); using var dispose = store;
        await using var runtime = new ChatRuntime(chat, workspace, store, command); await runtime.LoadHistory();
        var message = (await store.ReadPageAsync(chat)).Single(m => m.ProviderMessageId == "a1");
        var options = await runtime.HistoryOptions(message.Id, message.Sequence); Assert.True(options["point"]!.GetValue<bool>());
        var fork = await runtime.BranchHistory(message.Id, message.Sequence, options["checkpoint"]!.GetValue<string>(), true);
        Assert.NotEqual(chat.Id, fork.Id); Assert.Equal("original", chat.SessionId);
        Assert.Equal(4, chat.Messages.Count); Assert.Equal(2, fork.Messages.Count); Assert.Equal(2, store.Chats().Count);
        Assert.Equal("a1", (await store.ReadPageAsync(fork)).Last().ProviderMessageId);
    }
    [AvaloniaFact]
    public async Task FullChatForkAndFirstMessageEditUseSeparateSessions()
    {
        var (store, workspace, chat, command) = Setup(); using var dispose = store;
        await using var runtime = new ChatRuntime(chat, workspace, store, command); await runtime.LoadHistory();
        var fork = await runtime.BranchHistory(null, -1, null, true); Assert.Equal(4, fork.Messages.Count);
        var first = chat.Messages.First(); var options = await runtime.HistoryOptions(first.Id, first.Sequence);
        await runtime.BranchHistory(first.Id, first.Sequence, options["checkpoint"]!.GetValue<string>(), false);
        Assert.Empty(await store.ReadPageAsync(chat)); Assert.NotEqual("original", chat.SessionId);
    }
    [AvaloniaTheory]
    [InlineData("--reject-fork")]
    [InlineData("--reject-load")]
    [InlineData("--ignore-point")]
    public async Task FailedOrIncorrectForkLeavesOriginalHistoryIntact(string flags)
    {
        var (store, workspace, chat, command) = Setup(flags); using var dispose = store;
        await using var runtime = new ChatRuntime(chat, workspace, store, command); await runtime.LoadHistory();
        var original = chat.Messages.Select(m => m.Id).ToArray(); var message = chat.Messages[1];
        var options = await runtime.HistoryOptions(message.Id, message.Sequence);
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.BranchHistory(message.Id, message.Sequence, options["checkpoint"]!.GetValue<string>(), false));
        Assert.Equal("original", chat.SessionId); Assert.Equal(original, (await store.ReadPageAsync(chat)).Select(m => m.Id));
        Assert.Single(store.Chats()); Assert.False(runtime.IsChangingHistory); Assert.False(chat.Busy);
    }
    [AvaloniaFact]
    public async Task UnsupportedAdapterAndStaleSelectionDoNotRewriteHistory()
    {
        var (store, workspace, chat, command) = Setup("--no-fork"); using var dispose = store;
        await using var runtime = new ChatRuntime(chat, workspace, store, command); await runtime.LoadHistory();
        var message = chat.Messages[1]; var options = await runtime.HistoryOptions(message.Id, message.Sequence);
        Assert.False(options["fork"]!.GetValue<bool>()); Assert.False(options["point"]!.GetValue<bool>());
        await Assert.ThrowsAsync<IOException>(() => runtime.BranchHistory(message.Id, message.Sequence, "stale", false));
        Assert.Equal(4, (await store.ReadPageAsync(chat)).Length);
    }
}
