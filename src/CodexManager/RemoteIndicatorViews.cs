using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

internal static class RemoteIndicatorViews
{
    internal static void ShowScheduled(ChatComposer composer, int count, IReadOnlyList<RemoteScheduledIndicator> scheduled)
    {
        composer.ScheduledItems.Children.Clear();
        composer.ScheduledPanel.IsVisible = count > 0;
        composer.ScheduledPanel.Header = $"Scheduled sends ({count})";
        foreach (var send in scheduled)
        {
            var timing = GitHubWorkflowRun.Parse(send.WorkflowUrl) is { } run
                ? $"{(send.Waiting ? "Waiting for" : "Finished:")} {run.Repository} · #{run.Id}"
                : send.Due?.ToLocalTime().ToString("g") ?? "";
            if (send.CheckFailed) timing += " · Can't check run; verify GitHub CLI access on the host";
            composer.ScheduledItems.Children.Add(new StackPanel { Children =
            {
                new TextBlock { Text = send.Preview, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = timing, TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } }
            } });
        }
    }

    internal static void ShowGitHub(StackPanel rows, JsonNode? summary, Func<Uri, Task> open)
    {
        rows.Children.Clear();
        if (summary is null) return;
        foreach (var url in RemoteChatIndicators.Waits(summary))
            Add(url, "Waiting for checks · " + url["https://github.com/".Length..].Replace("/pull/", " #", StringComparison.Ordinal), AppIcons.Create("schedule"));
        foreach (var pr in RemoteChatIndicators.PullRequests(summary))
            Add(pr.Url, Title(pr.Url, pr.Title), MainView.PullRequestIcon(pr));
        foreach (var issue in RemoteChatIndicators.Issues(summary))
            Add(issue.Url, Title(issue.Url, issue.Title), AppIcons.Create(issue.State == "CLOSED" ? "issue-closed" : "issue-opened"));

        static string Title(string url, string title) => "#" + url[(url.LastIndexOf('/') + 1)..] + " · " + title;
        void Add(string url, string title, Control icon)
        {
            var label = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            var content = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Children = { icon, label } };
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 2) };
            ToolTip.SetTip(button, url);
            button.Click += async (_, _) => await open(new Uri(url));
            rows.Children.Add(button);
        }
    }
}
