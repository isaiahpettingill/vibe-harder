using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Message, object> scannedIssueTools = new();
    private string issueSignature = "";
    private void UpdateIssuePane()
    {
        if (current is { IsRemote: false } chat)
        {
            ChatIssues.Load(store, chat);
            foreach (var message in chat.Messages.Where(m => m.Role == "tool" && m.Text.Contains("*completed*", StringComparison.Ordinal)))
            {
                if (scannedIssueTools.TryGetValue(message, out _)) continue;
                scannedIssueTools.Add(message, new object());
                if (AcpToolCall.FromText(message.Text).UpdatedGitHubIssues() is { } change)
                    foreach (var url in change.Urls) ChatIssues.Add(store, chat, url);
            }
        }
        var requests = current?.Issues.Where(p => !p.Dismissed).ToArray() ?? [];
        var signature = current?.Id + "\n" + string.Join("\n", requests.Select(p => p.ToString()));
        if (signature == issueSignature) return;
        issueSignature = signature;
        IssuePane.Children.Clear(); IssueScroll.IsVisible = requests.Length > 0;
        foreach (var request in requests)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 };
            var label = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
            label.Children.Add(AppIcons.Create(request.State == "CLOSED" ? "issue-closed" : "issue-opened"));
            var title = new TextBlock { Text = "#" + request.Url[(request.Url.LastIndexOf('/') + 1)..] + " · " + request.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 1); label.Children.Add(title);
            var link = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2) };
            ToolTip.SetTip(link, request.Url + (request.State == "CLOSED" ? " · Closed" : ""));
            link.Click += async (_, _) => { if (Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(request.Url)); };
            row.Children.Add(link);
            if (request.State == "CLOSED" && current is { IsRemote: false } owner)
            {
                var dismiss = new IconButton { Icon = "remove", Label = "Dismiss closed issue" };
                dismiss.Click += (_, _) => { ChatIssues.Dismiss(store, owner, request.Url); UpdateIssuePane(); };
                Grid.SetColumn(dismiss, 1); row.Children.Add(dismiss);
            }
            IssuePane.Children.Add(row);
        }
    }
}
