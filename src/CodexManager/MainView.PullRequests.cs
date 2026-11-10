using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private static readonly IBrush openPullRequestBrush = Brush.Parse("#3fb950");
    private static readonly IBrush mergedPullRequestBrush = Brush.Parse("#a371f7");
    private static readonly IBrush closedPullRequestBrush = Brush.Parse("#f85149");
    private static PathIcon PullRequestIcon(ChatPullRequest request)
    {
        var icon = AppIcons.Create(request.State switch { "MERGED" => "git-merge", "CLOSED" => "git-pull-request-closed", _ => "git-pull-request" });
        icon.Foreground = request.State switch { "MERGED" => mergedPullRequestBrush, "CLOSED" => closedPullRequestBrush, _ => openPullRequestBrush };
        ToolTip.SetTip(icon, request.Title + " · " + request.State.ToLowerInvariant());
        return icon;
    }
    // One sidebar icon stands for every pull request of a chat; hovering summarizes them.
    private static PathIcon PullRequestSummaryIcon(IReadOnlyList<ChatPullRequest> requests)
    {
        var state = requests.Any(p => p.State == "OPEN") ? "OPEN" : requests.Any(p => p.State == "MERGED") ? "MERGED" : "CLOSED";
        var icon = AppIcons.Create(state switch { "MERGED" => "git-merge", "CLOSED" => "git-pull-request-closed", _ => "git-pull-request" });
        icon.Foreground = state switch { "MERGED" => mergedPullRequestBrush, "CLOSED" => closedPullRequestBrush, _ => openPullRequestBrush };
        ToolTip.SetTip(icon, PullRequestSummary(requests));
        return icon;
    }
    private static string PullRequestSummary(IReadOnlyList<ChatPullRequest> requests)
    {
        var counts = new[] { ("OPEN", "open"), ("MERGED", "merged"), ("CLOSED", "closed") }
            .Select(state => (state.Item2, Count: requests.Count(p => p.State == state.Item1))).Where(state => state.Count > 0)
            .Select(state => state.Count + " " + state.Item2);
        var lines = new List<string> { requests.Count + (requests.Count == 1 ? " pull request · " : " pull requests · ") + string.Join(", ", counts) };
        lines.AddRange(requests.Select(p => "#" + p.Url[(p.Url.LastIndexOf('/') + 1)..] + " " + (p.Title.Length > 60 ? p.Title[..59] + "…" : p.Title) + " · " + p.State.ToLowerInvariant()));
        return string.Join("\n", lines);
    }
    private bool refreshingPullRequests;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Message, object> scannedPullRequestTools = new();
    private string pullRequestSignature = "";
    private void ConfigurePullRequests()
    {
        var timer = new DispatcherTimer { Interval = GitHubRefreshSchedule.Interval };
        timer.Tick += async (_, _) => await RefreshPullRequests();
        AttachedToVisualTree += (_, _) => { timer.Start(); _ = RefreshPullRequests(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }
    private async Task RefreshPullRequests()
    {
        if (closing || refreshingPullRequests) return;
        refreshingPullRequests = true;
        try
        {
            foreach (var chat in chats.Where(c => !c.IsRemote).ToArray())
                if (!closing && workspaces.FirstOrDefault(w => w.Id == chat.WorkspaceId) is { } owner)
                {
                    await ChatPullRequests.Refresh(store, chat, owner, discoveryLifetime.Token);
                    await ChatIssues.Refresh(store, chat, owner, discoveryLifetime.Token);
                }
            if (!closing) { UpdatePullRequestPane(); UpdateIssuePane(); }
        }
        finally { refreshingPullRequests = false; }
    }
    private void UpdatePullRequestPane()
    {
        if (current is { IsRemote: false } chat)
        {
            ChatPullRequests.Load(store, chat);
            foreach (var message in chat.Messages.Where(m => m.Role == "tool" && m.Text.Contains("*completed*", StringComparison.Ordinal)))
            {
                if (scannedPullRequestTools.TryGetValue(message, out _)) continue;
                scannedPullRequestTools.Add(message, new object());
                foreach (var url in AcpToolCall.FromText(message.Text).CreatedGitHubPullRequests()) ChatPullRequests.Add(store, chat, url);
            }
        }
        var requests = current?.PullRequests.Where(p => !p.Dismissed).ToArray() ?? [];
        var signature = current?.Id + "\n" + string.Join("\n", requests.Select(p => p.ToString()));
        if (signature == pullRequestSignature) return;
        pullRequestSignature = signature;
        PullRequestPane.Children.Clear(); PullRequestScroll.IsVisible = requests.Length > 0;
        foreach (var request in requests)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 };
            var label = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
            label.Children.Add(PullRequestIcon(request));
            var title = new TextBlock { Text = "#" + request.Url[(request.Url.LastIndexOf('/') + 1)..] + " · " + request.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 1); label.Children.Add(title);
            var link = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2) };
            ToolTip.SetTip(link, request.Url + (request.State == "CLOSED" ? " · Closed" : ""));
            link.Click += async (_, _) => { if (Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(request.Url)); };
            row.Children.Add(link);
            if (request.State is "CLOSED" or "MERGED" && current is { IsRemote: false } owner)
            {
                var dismiss = new IconButton { Icon = "remove", Label = "Dismiss pull request" };
                dismiss.Click += (_, _) => { ChatPullRequests.Dismiss(store, owner, request.Url); UpdatePullRequestPane(); };
                Grid.SetColumn(dismiss, 1); row.Children.Add(dismiss);
            }
            PullRequestPane.Children.Add(row);
        }
    }
}
