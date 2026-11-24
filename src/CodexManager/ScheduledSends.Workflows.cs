using System.Text.Json;

namespace CodexManager;

internal sealed partial class ScheduledSends
{
    private static readonly TimeSpan RunCheckInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset nextRunCheck;
    private bool checkingRuns;
    private bool disposed;

    internal bool HasRunWait(string chatId) => ByChat[chatId].Any(send => send.ResumeAfterRun);

    internal async Task<GitHubWorkflowRun> SetRunWait(string chatId, string url, string runId, bool cancel)
    {
        var chat = chats.FirstOrDefault(chat => chat.Id == chatId && !chat.Archived && !chat.IsRemote && !chat.IsDeleting)
            ?? throw new IOException("This command must run in an active Vibe Harder agent chat.");
        var run = runId.Length == 0
            ? GitHubWorkflowRun.Parse(url) ?? throw new IOException("Enter a GitHub Actions run URL or an OWNER/REPO and a positive run ID.")
            : await GitHubWorkflowRun.FromOrigin(workspaces.FirstOrDefault(owner => owner.Id == chat.WorkspaceId && !owner.IsRemote)
                ?? throw new IOException("Open the chat's workspace to use a bare run ID."), runId);
        if (chat.IsDeleting || chat.Archived || !chats.Contains(chat)) throw new IOException("This chat is no longer active.");
        var existing = ForChat(chatId).Where(send => send.ResumeAfterRun).ToArray();
        var wait = existing.FirstOrDefault(send => send.Schedule.WorkflowRunUrl == run.Url);
        if (cancel)
        {
            if (wait is not null) Remove(wait.Id);
            return run;
        }
        if (wait is not null) return run;
        if (existing.Length >= 10) throw new IOException("This chat already has ten GitHub Actions waits.");
        Add(chatId, new($"GitHub Actions run {run.Url} finished. Check its conclusion before continuing the original task, and act only within the user's existing authorization. This notification does not authorize a merge or deployment.", []),
            SendSchedule.After(run), resumeAfterRun: true);
        return run;
    }

    internal async Task CheckRuns(Func<Workspace, GitHubWorkflowRun, Task<bool>>? read = null)
    {
        if (checkingRuns || disposed) return;
        checkingRuns = true;
        try
        {
            // Check each run once per host, even if several chats are waiting on it.
            var runs = Entries.Where(send => send.WaitingForRun)
                .Select(send => (Send: send, Run: GitHubWorkflowRun.Parse(send.Schedule.WorkflowRunUrl), Target: TargetFor(send)))
                .Where(item => item.Run is not null && item.Target is not null)
                .GroupBy(item => (item.Run!, item.Target!.Value.Workspace.Distro)).ToArray();
            foreach (var group in runs)
            {
                if (disposed) return;
                var run = group.Key.Item1;
                DateTimeOffset? completedAt = null;
                var failed = false;
                try
                {
                    var owner = group.First().Target!.Value.Workspace;
                    if (await (read is null ? run.IsComplete(owner) : read(owner, run))) completedAt = DateTimeOffset.UtcNow;
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    failed = true;
                    AppDiagnostics.Record("Check scheduled GitHub Actions run", error);
                }
                if (disposed) return;
                var matched = group.Select(item => item.Send.Id).ToHashSet();
                foreach (var send in Entries.Where(send => send.WaitingForRun && matched.Contains(send.Id) && send.Schedule.WorkflowRunUrl == run.Url).ToArray())
                {
                    if (completedAt is null && send.RunCheckFailed == failed) continue;
                    var updated = send with { Due = completedAt ?? send.Due, RunCheckFailed = failed };
                    var entries = Entries.Select(entry => entry.Id == send.Id ? updated : entry).ToArray();
                    if (completedAt is not null) Save(send.ChatId, entries);
                    else UpdateEntries(send.ChatId, entries);
                }
            }
        }
        finally { nextRunCheck = DateTimeOffset.UtcNow + RunCheckInterval; checkingRuns = false; }
    }
}
