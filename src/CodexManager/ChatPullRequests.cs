using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

public sealed record ChatPullRequest(string Url, string Title, string State = "OPEN", bool Dismissed = false);

public static class ChatPullRequests
{
    public static bool ValidUrl(string url) => Regex.IsMatch(url, @"\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[1-9][0-9]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static void Load(Store store, Chat chat)
    {
        if (chat.PullRequestsLoaded || chat.IsRemote) return;
        chat.PullRequestsLoaded = true;
        try
        {
            if (JsonNode.Parse(store.Setting("pullRequests:" + chat.Id) ?? "[]") is JsonArray saved)
                foreach (var item in saved.OfType<JsonObject>())
                    if (item["url"]?.GetValue<string>() is { } url && ValidUrl(url))
                        chat.PullRequests.Add(new(url, item["title"]?.GetValue<string>() ?? url, item["state"]?.GetValue<string>() ?? "OPEN", item["dismissed"]?.GetValue<bool>() == true));
        }
        catch (System.Text.Json.JsonException error) { AppDiagnostics.Record("Load pull requests", error); }
        chat.PullRequestsChanged();
    }
    public static JsonArray ToJson(Chat chat) => new(chat.PullRequests.Select(p => (JsonNode)new JsonObject { ["url"] = p.Url, ["title"] = p.Title, ["state"] = p.State, ["dismissed"] = p.Dismissed }).ToArray());
    private static void Save(Store store, Chat chat)
    {
        store.Setting("pullRequests:" + chat.Id, ToJson(chat).ToJsonString()); chat.PullRequestsChanged();
    }
    public static void Add(Store store, Chat chat, string url)
    {
        if (!ValidUrl(url)) return;
        Load(store, chat);
        if (chat.PullRequests.Any(p => p.Url == url)) return;
        chat.PullRequests.Add(new(url, url.Replace("https://github.com/", "").Replace("/pull/", " #")));
        Save(store, chat);
    }
    public static void Apply(Store store, Chat chat, string url, string title, string state)
    {
        Load(store, chat);
        var index = chat.PullRequests.FindIndex(p => p.Url == url);
        if (index < 0 || state is not ("OPEN" or "CLOSED" or "MERGED")) return;
        var previous = chat.PullRequests[index];
        var updated = previous with { Title = title, State = state, Dismissed = state is "CLOSED" or "MERGED" && previous.Dismissed };
        if (previous == updated) return;
        chat.PullRequests[index] = updated; Save(store, chat);
    }
    // An agent-reported merge, matched to a tracked request by URL or by "#<number>" when the repository is unknown.
    public static bool Merged(Store store, Chat chat, string reference)
    {
        Load(store, chat);
        var target = chat.PullRequests.FirstOrDefault(p => p.Url == reference || reference.StartsWith('#') && p.Url.EndsWith("/pull/" + reference[1..], StringComparison.Ordinal));
        if (target is null || target.State == "MERGED") return false;
        chat.PullRequests[chat.PullRequests.IndexOf(target)] = target with { State = "MERGED" };
        Save(store, chat);
        return true;
    }
    public static void Dismiss(Store store, Chat chat, string url)
    {
        var index = chat.PullRequests.FindIndex(p => p.Url == url && p.State is "CLOSED" or "MERGED");
        if (index < 0) return;
        chat.PullRequests[index] = chat.PullRequests[index] with { Dismissed = true }; Save(store, chat);
    }
    public static async Task Refresh(Store store, Chat chat, Workspace workspace, CancellationToken token = default)
    {
        Load(store, chat);
        foreach (var pullRequest in chat.PullRequests.Where(p => p.State != "MERGED" && !p.Dismissed).ToArray())
        {
            if (token.IsCancellationRequested) return;
            if (!ValidUrl(pullRequest.Url) || !GitHubRefreshSchedule.For(chat).TryStart(pullRequest.Url, DateTimeOffset.UtcNow)) continue;
            try
            {
                var json = await Hosts.Capture(Hosts.Agent(workspace, "gh pr view " + pullRequest.Url + " --json title,state"), TimeSpan.FromSeconds(15));
                if (token.IsCancellationRequested) return;
                if (JsonNode.Parse(json) is JsonObject result && result["title"]?.GetValue<string>() is { } title && result["state"]?.GetValue<string>() is { } state)
                    Apply(store, chat, pullRequest.Url, title, state);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            { AppDiagnostics.Record("Refresh pull request", error); }
        }
    }
}
