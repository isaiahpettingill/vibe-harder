using System.Text.Json.Nodes;

namespace CodexManager;

internal sealed record RemoteScheduledIndicator(string Preview, DateTimeOffset? Due, string? WorkflowUrl, bool Waiting, bool CheckFailed);

internal static class RemoteChatIndicators
{
    internal static void Write(Store store, Chat chat, ScheduledSends schedules, PullRequestWaits waits, JsonObject summary, bool detail)
    {
        ChatPullRequests.Load(store, chat);
        ChatIssues.Load(store, chat);
        summary["pullRequests"] = new JsonArray(chat.PullRequests.Where(pr => !pr.Dismissed).Select(pr => Link(pr.Url, pr.Title, pr.State)).ToArray());
        summary["issues"] = new JsonArray(chat.Issues.Where(issue => !issue.Dismissed).Select(issue => Link(issue.Url, issue.Title, issue.State)).ToArray());
        summary["pullRequestWaits"] = new JsonArray(waits.Pending(chat).Select(url => (JsonNode)JsonValue.Create(url)!).ToArray());
        summary["pullRequestOpenedAt"] = chat.PullRequestOpenedAt?.ToString("O");
        if (!detail) return;
        summary["scheduledSends"] = new JsonArray(schedules.ForChat(chat.Id).Select(send => (JsonNode)new JsonObject
        {
            ["preview"] = Preview(send), ["due"] = send.WaitingForRun ? null : send.Due.ToString("O"),
            ["workflowUrl"] = send.Schedule.WorkflowRunUrl, ["waiting"] = send.WaitingForRun, ["checkFailed"] = send.RunCheckFailed
        }).ToArray());
    }

    private static string Preview(ScheduledSend send)
    {
        var text = send.Input.Text.AsSpan();
        var end = text.IndexOfAny('\r', '\n');
        var preview = end < 0 ? text : text[..end];
        return preview.IsEmpty ? send.Input.Attachments.FirstOrDefault()?.Name ?? "" : preview[..Math.Min(preview.Length, 240)].ToString();
    }

    private static JsonNode Link(string url, string title, string state) => new JsonObject { ["url"] = url, ["title"] = title, ["state"] = state };
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => node is JsonArray rows ? rows.OfType<JsonObject>() : [];
    internal static ChatPullRequest[] PullRequests(JsonNode summary) => Rows(summary["pullRequests"])
        .Where(pr => Text(pr["url"]) is { } url && ChatPullRequests.ValidUrl(url) && Text(pr["state"]) is "OPEN" or "CLOSED" or "MERGED")
        .Select(pr => new ChatPullRequest(Text(pr["url"])!, Text(pr["title"]) ?? Text(pr["url"])!, Text(pr["state"])!)).ToArray();
    internal static ChatIssue[] Issues(JsonNode summary) => Rows(summary["issues"])
        .Where(issue => Text(issue["url"]) is { } url && ChatIssues.ValidUrl(url) && Text(issue["state"]) is "OPEN" or "CLOSED")
        .Select(issue => new ChatIssue(Text(issue["url"])!, Text(issue["title"]) ?? Text(issue["url"])!, Text(issue["state"])!)).ToArray();
    internal static string[] Waits(JsonNode summary) => summary["pullRequestWaits"] is JsonArray waits
        ? waits.Select(Text).OfType<string>().Where(ChatPullRequests.ValidUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    internal static int ScheduledCount(JsonNode summary) => summary["scheduled"] is JsonValue value && value.TryGetValue<int>(out var count) ? Math.Max(0, count) : 0;
    internal static RemoteScheduledIndicator[] Scheduled(JsonNode summary) => Rows(summary["scheduledSends"]).Select(send => new RemoteScheduledIndicator(
        Text(send["preview"]) ?? "", DateTimeOffset.TryParse(Text(send["due"]), out var due) ? due : null,
        GitHubWorkflowRun.Parse(Text(send["workflowUrl"]))?.Url, Flag(send["waiting"]), Flag(send["checkFailed"]))).ToArray();

    internal static void Apply(Chat chat, JsonNode summary)
    {
        chat.RemoteScheduledSendCount = ScheduledCount(summary);
        var pullRequests = PullRequests(summary);
        if (!chat.PullRequests.SequenceEqual(pullRequests))
        {
            chat.PullRequests.Clear(); chat.PullRequests.AddRange(pullRequests); chat.PullRequestsChanged();
        }
        var issues = Issues(summary);
        if (!chat.Issues.SequenceEqual(issues)) { chat.Issues.Clear(); chat.Issues.AddRange(issues); }
        var waits = Waits(summary);
        if (!chat.RemotePullRequestWaits.SequenceEqual(waits)) chat.RemotePullRequestWaits = waits;
        if (summary["scheduledSends"] is JsonArray) chat.RemoteScheduledIndicators = Scheduled(summary);
        chat.PullRequestOpenedAt = DateTimeOffset.TryParse(Text(summary["pullRequestOpenedAt"]), out var opened) ? opened : null;
    }
}
