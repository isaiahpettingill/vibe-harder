using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

public sealed record ChatIssue(string Url, string Title, string State = "OPEN", bool Dismissed = false);

public sealed record GitHubIssueCommand(string[] Urls, string? Number, string? Repository);

public static class ChatIssues
{
    public static bool ValidUrl(string url) => Regex.IsMatch(url, @"\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/issues/[1-9][0-9]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static void Load(Store store, Chat chat)
    {
        if (chat.IssuesLoaded || chat.IsRemote) return;
        chat.IssuesLoaded = true;
        try
        {
            if (JsonNode.Parse(store.Setting("issues:" + chat.Id) ?? "[]") is JsonArray saved)
                foreach (var item in saved.OfType<JsonObject>())
                    if (item["url"]?.GetValue<string>() is { } url && ValidUrl(url))
                        chat.Issues.Add(new(url, item["title"]?.GetValue<string>() ?? url, item["state"]?.GetValue<string>() ?? "OPEN", item["dismissed"]?.GetValue<bool>() == true));
        }
        catch (System.Text.Json.JsonException error) { AppDiagnostics.Record("Load issues", error); }

    }
    public static JsonArray ToJson(Chat chat) => new(chat.Issues.Select(p => (JsonNode)new JsonObject { ["url"] = p.Url, ["title"] = p.Title, ["state"] = p.State, ["dismissed"] = p.Dismissed }).ToArray());
    private static void Save(Store store, Chat chat)
    {
        store.Setting("issues:" + chat.Id, ToJson(chat).ToJsonString());
    }
    public static void Add(Store store, Chat chat, string url)
    {
        if (!ValidUrl(url)) return;
        Load(store, chat);
        if (chat.Issues.Any(p => p.Url == url)) return;
        chat.Issues.Add(new(url, url["https://github.com/".Length..url.IndexOf("/issues/", StringComparison.Ordinal)]));
        Save(store, chat);
    }
    public static void Apply(Store store, Chat chat, string url, string title, string state)
    {
        Load(store, chat);
        var index = chat.Issues.FindIndex(p => p.Url == url);
        if (index < 0 || state is not ("OPEN" or "CLOSED")) return;
        var previous = chat.Issues[index];
        var updated = previous with { Title = title, State = state, Dismissed = state == "CLOSED" && previous.Dismissed };
        if (previous == updated) return;
        chat.Issues[index] = updated; Save(store, chat);
    }
    public static void Dismiss(Store store, Chat chat, string url)
    {
        var index = chat.Issues.FindIndex(p => p.Url == url && p.State == "CLOSED");
        if (index < 0) return;
        chat.Issues[index] = chat.Issues[index] with { Dismissed = true }; Save(store, chat);
    }
    public static async Task Track(Store store, Chat chat, Workspace workspace, GitHubIssueCommand command, Action changed, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        foreach (var url in command.Urls) Add(store, chat, url);
        if (command.Urls.Length > 0) changed();
        if (command.Number is not { } number || command.Urls.Length > 0) return;
        if (!Regex.IsMatch(number, @"\A[1-9][0-9]*\z")) return;
        if (command.Repository is { } requestedRepo && !Regex.IsMatch(requestedRepo, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")) return;
        try
        {
            var repo = command.Repository is { } repository ? " --repo " + repository : "";
            var json = await Hosts.Capture(Hosts.Agent(workspace, "gh issue view " + number + repo + " --json url,title,state"), TimeSpan.FromSeconds(15));
            if (token.IsCancellationRequested) return;
            if (JsonNode.Parse(json) is JsonObject result && result["url"]?.GetValue<string>() is { } url && ValidUrl(url))
            {
                Add(store, chat, url);
                if (result["title"]?.GetValue<string>() is { } title && result["state"]?.GetValue<string>() is { } state) Apply(store, chat, url, title, state);
                changed();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        { AppDiagnostics.Record("Resolve issue", error); }
    }
    public static async Task Refresh(Store store, Chat chat, Workspace workspace, CancellationToken token = default)
    {
        Load(store, chat);
        foreach (var issue in chat.Issues.Where(p => !p.Dismissed).ToArray())
        {
            if (token.IsCancellationRequested) return;
            if (!ValidUrl(issue.Url)) continue;
            try
            {
                var json = await Hosts.Capture(Hosts.Agent(workspace, "gh issue view " + issue.Url + " --json title,state"), TimeSpan.FromSeconds(15));
                if (token.IsCancellationRequested) return;
                if (JsonNode.Parse(json) is JsonObject result && result["title"]?.GetValue<string>() is { } title && result["state"]?.GetValue<string>() is { } state)
                    Apply(store, chat, issue.Url, title, state);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            { AppDiagnostics.Record("Refresh issue", error); }
        }
    }
}
