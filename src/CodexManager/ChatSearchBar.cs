using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace CodexManager;

public sealed record ChatSearchHit(string Id, int Sequence, string Preview);

public sealed class ChatSearchBar : Border
{
    private readonly TextBox query = new() { Name = "ChatSearchQuery", PlaceholderText = "Search in chat…", MinWidth = 80 };
    private readonly TextBlock count = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly IconButton previous = new() { Icon = "chevron-up", Label = "Previous match" };
    private readonly IconButton next = new() { Name = "NextChatSearchMatch", Icon = "chevron-down", Label = "Next match" };
    private CancellationTokenSource? pending;
    private CancellationTokenSource? navigating;
    private ChatSearchHit[] matches = [];
    private int selected = -1;
    public Func<string, CancellationToken, Task<ChatSearchHit[]>>? Search { get; set; }
    public Func<ChatSearchHit, CancellationToken, Task>? Navigate { get; set; }
    public ChatSearchBar()
    {
        IsVisible = false; Padding = new(4); Margin = new(0, 0, 0, 4);
        var row = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 4 };
        var close = new IconButton { Icon = "remove", Label = "Close chat search" };
        row.Children.Add(query); Grid.SetColumn(count, 1); row.Children.Add(count);
        Grid.SetColumn(previous, 2); row.Children.Add(previous); Grid.SetColumn(next, 3); row.Children.Add(next);
        Grid.SetColumn(close, 4); row.Children.Add(close); Child = row;
        query.TextChanged += async (_, _) => await Find();
        previous.Click += async (_, _) => await Move(-1); next.Click += async (_, _) => await Move(1);
        close.Click += (_, _) => Close();
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; await Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
        };
        DetachedFromVisualTree += (_, _) => Cancel();
    }
    public void Open() { IsVisible = true; query.Focus(); query.SelectAll(); _ = Find(); }
    public void Close() { IsVisible = false; Cancel(); matches = []; selected = -1; count.Text = ""; }
    private void Cancel() { pending?.Cancel(); pending?.Dispose(); pending = null; }
    private async Task Find()
    {
        Cancel(); matches = []; selected = -1; previous.IsEnabled = next.IsEnabled = false;
        if (!IsVisible || string.IsNullOrWhiteSpace(query.Text) || Search is null) { count.Text = ""; return; }
        var request = new CancellationTokenSource(); pending = request;
        try
        {
            count.Text = "Searching…";
            await Task.Delay(150, request.Token);
            var found = await Search(query.Text, request.Token);
            request.Token.ThrowIfCancellationRequested();
            matches = found;
            previous.IsEnabled = next.IsEnabled = matches.Length > 0;
            if (matches.Length == 0) { count.Text = "No matches"; return; }
            await Move(1);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!request.IsCancellationRequested) count.Text = error.Message; }
    }
    private async Task Move(int direction)
    {
        if (matches.Length == 0 || Navigate is null) return;
        selected = (selected + direction + matches.Length) % matches.Length;
        count.Text = $"{selected + 1} / {matches.Length}" + (matches.Length == 200 ? "+" : "");
        ToolTip.SetTip(count, matches[selected].Preview);
        navigating?.Cancel();
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(pending?.Token ?? CancellationToken.None); navigating = navigation;
        try { await Navigate(matches[selected], navigation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { count.Text = error.Message; }
        finally { if (ReferenceEquals(navigating, navigation)) navigating = null; }
    }
}
