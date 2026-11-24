using System.Text.Json.Nodes;

namespace CodexManager;

internal sealed class PullRequestWaits(Store store, IList<Workspace> workspaces, IList<Chat> chats,
    Func<Chat, Workspace, ChatRuntime> runtime)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private bool refreshing;
    internal event Action<Chat>? Changed;
    internal string[] Pending(Chat chat) => Read(chat).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string Key(Chat chat) => "pullRequestWaits:" + chat.Id;
    internal static bool IsWaiting(Store store, Chat chat) => store.Setting(Key(chat)) is not (null or "[]");

    internal JsonObject Set(string chatId, string repository, string number, bool cancel)
    {
        var url = "https://github.com/" + repository.ToLowerInvariant() + "/pull/" + number;
        if (repository.Split('/').Length != 2 || !ChatPullRequests.ValidUrl(url))
            throw new IOException("Enter a GitHub repository as OWNER/REPO and a positive pull request number.");
        var chat = chats.FirstOrDefault(c => c.Id == chatId && !c.Archived && !c.IsRemote && !c.IsDeleting)
            ?? throw new IOException("This command must run in an active Vibe Harder agent chat.");
        var urls = Read(chat);
        if (cancel) urls.Remove(url);
        else if (!urls.Contains(url))
        {
            if (urls.Count >= 10) throw new IOException("This chat already has ten pull request waits.");
            urls.Add(url);
        }
        Save(chat, urls);
        return new JsonObject { ["url"] = url, ["waiting"] = !cancel };
    }

    private HashSet<string> Read(Chat chat) => (JsonNode.Parse(store.Setting(Key(chat)) ?? "[]") as JsonArray ?? [])
        .Select(v => v?.GetValue<string>()).OfType<string>().Where(ChatPullRequests.ValidUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void Save(Chat chat, HashSet<string> urls)
    {
        store.Setting(Key(chat), new JsonArray(urls.Select(url => (JsonNode)JsonValue.Create(url)!).ToArray()).ToJsonString());
        Changed?.Invoke(chat);
    }

    internal async Task Run(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do { await Refresh(token); } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task Refresh(CancellationToken token,
        Func<Workspace, string, Task<JsonObject?>>? read = null,
        Func<Chat, Workspace, PendingInput, Task>? send = null)
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            foreach (var chat in chats.Where(Available).ToArray())
            {
                if (token.IsCancellationRequested) return;
                if (workspaces.FirstOrDefault(w => w.Id == chat.WorkspaceId && store.Setting("closed:" + w.Id) != "1") is not { } owner) continue;
                foreach (var url in Read(chat))
                {
                    if (!Available(chat) || token.IsCancellationRequested) break;
                    try
                    {
                        var result = await (read ?? ReadGitHub)(owner, url);
                        if (!Available(chat) || token.IsCancellationRequested || result is null) continue;
                        var urls = Read(chat);
                        if (!urls.Contains(url)) continue;
                        var state = result["state"]?.GetValue<string>();
                        if (state is "CLOSED" or "MERGED") { urls.Remove(url); Save(chat, urls); continue; }
                        if (state != "OPEN" || Completed(result) is not { } completed) continue;
                        if (send is null && runtime(chat, owner) is ({ IsPreparing: true } or { IsConfiguring: true })) continue;
                        var input = new PendingInput($"GitHub checks finished for {url} at commit {completed.Head} ({(completed.Passed ? "passed" : "not all passed")}). Recheck the current PR head and repository gates. Continue the original task only within the user's existing authorization; this notification does not authorize a merge or deployment.", []);
                        chat.QueuedInputs.Add(input);
                        store.Save(chat);
                        urls.Remove(url); Save(chat, urls);
                        if (send is null) await runtime(chat, owner).SendQueuedNow(input, waitForCompletion: false);
                        else await send(chat, owner, input);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { AppDiagnostics.Record("Check pull request wait", error); }
                }
            }
        }
        finally { refreshing = false; }
    }

    private bool Available(Chat chat) => chats.Contains(chat) && !chat.Archived && !chat.IsDeleting && !chat.IsRemote && !chat.Busy && !chat.NeedsLogin && chat.InterruptedInput is null && chat.QueuedInputs.Count == 0;

    private static async Task<JsonObject?> ReadGitHub(Workspace workspace, string url) => JsonNode.Parse(await Hosts.Capture(
        Hosts.Agent(workspace, "gh pr view " + url + " --json state,headRefOid,statusCheckRollup"), TimeSpan.FromSeconds(15))) as JsonObject;

    internal static (string Head, bool Passed)? Completed(JsonObject result)
    {
        if (result["headRefOid"]?.GetValue<string>() is not { Length: 40 } head || !head.All(char.IsAsciiHexDigit) ||
            result["statusCheckRollup"] is not JsonArray { Count: > 0 } checks) return null;
        var passed = true;
        foreach (var node in checks)
        {
            if (node is not JsonObject check) return null;
            if (check["status"]?.GetValue<string>() is { } status)
            {
                if (status != "COMPLETED" || check["conclusion"]?.GetValue<string>() is not { Length: > 0 } conclusion) return null;
                passed &= conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED";
            }
            else if (check["state"]?.GetValue<string>() is "SUCCESS" or "ERROR" or "FAILURE")
                passed &= check["state"]?.GetValue<string>() == "SUCCESS";
            else return null;
        }
        return (head, passed);
    }
}
