using System.Runtime.CompilerServices;

namespace CodexManager;

public sealed class GitHubRefreshSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly ConditionalWeakTable<Chat, GitHubRefreshSchedule> schedules = new();
    private readonly Dictionary<string, DateTimeOffset> attempts = [];
    public static GitHubRefreshSchedule For(Chat chat) => schedules.GetValue(chat, _ => new());
    public bool TryStart(string url, DateTimeOffset now)
    {
        if (attempts.TryGetValue(url, out var previous) && now - previous < Interval) return false;
        attempts[url] = now;
        return true;
    }
}
