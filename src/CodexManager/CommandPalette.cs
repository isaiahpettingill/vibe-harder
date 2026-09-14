using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed record PaletteCommand(string Title, string Detail, Func<Task> Execute);

public sealed class CommandPalette : UserControl
{
    private readonly TextBox query = new() { Name = "CommandQuery", PlaceholderText = "Search commands or configuration files…" };
    private readonly ListBox results = new() { Name = "CommandResults" };
    private readonly TextBlock status = new() { Text = "Finding configuration files…", FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly List<PaletteCommand> commands;
    private readonly TaskCompletionSource<PaletteCommand?> completion = new();
    private PaletteCommand? chosen;
    private Panel? host;
    private bool closed;
    private IInputElement? previousFocus;
    public void Activate() => query.Focus();
    public Task<PaletteCommand?> Open(Panel owner)
    {
        if (host is not null || closed) return completion.Task;
        previousFocus = TopLevel.GetTopLevel(owner)?.FocusManager?.GetFocusedElement();
        host = owner; Grid.SetColumnSpan(this, Math.Max(1, (owner as Grid)?.ColumnDefinitions.Count ?? 1)); Grid.SetRowSpan(this, Math.Max(1, (owner as Grid)?.RowDefinitions.Count ?? 1));
        ZIndex = 1000; owner.Children.Add(this); Activate();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!closed) Activate(); }, Avalonia.Threading.DispatcherPriority.Loaded);
        return completion.Task;
    }
    public void Close()
    {
        if (closed) return;
        closed = true; IsVisible = false;
        var owner = host; host = null; owner?.Children.Remove(this);
        previousFocus?.Focus(); completion.TrySetResult(chosen);
    }
    public CommandPalette(IEnumerable<PaletteCommand> initial, Workspace? workspace)
    {
        commands = initial.ToList();
        Name = "CommandPaletteOverlay"; HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var grid = new Grid { Margin = new Thickness(10), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 6 };
        grid.Children.Add(query); Grid.SetRow(results, 1); grid.Children.Add(results); Grid.SetRow(status, 2); grid.Children.Add(status);
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)) };
        var card = new Border { Child = grid, MaxWidth = 700, Height = 440, Margin = new Thickness(16, 40, 16, 16), VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBackground")); card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorder"));
        overlay.Children.Add(card); Content = overlay;
        overlay.SizeChanged += (_, _) => card.MaxHeight = Math.Max(80, overlay.Bounds.Height - 56);
        overlay.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, overlay)) { e.Handled = true; Close(); } };
        DetachedFromVisualTree += (_, _) => Close();
        results.ItemTemplate = new FuncDataTemplate<PaletteCommand>((command, _) => new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 4),
            Children = {
                new TextBlock { Text = command?.Title },
                new TextBlock { Text = command?.Detail, FontSize = 11, Opacity = .6, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        query.TextChanged += (_, _) => Filter();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Choose(); }
            else if (e.Key is Key.Down or Key.Up) { e.Handled = true; results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, results.ItemCount - 1)); results.ScrollIntoView(results.SelectedItem!); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        results.DoubleTapped += (_, _) => Choose();
        Filter();
        AttachedToVisualTree += async (_, _) =>
        {
            query.Focus();
            try
            {
                var files = await ConfigurationFiles.Find(workspace);
                if (!IsVisible) return;
                commands.AddRange(files.Select(file => new PaletteCommand(file.Title, file.Path, () => { ConfigurationFiles.Open(file); return Task.CompletedTask; })));
                status.Text = $"{workspace?.Host ?? "Local"} · {files.Count} configuration files · Enter to run · Esc to close";
                Filter();
            }
            catch (Exception error) { if (IsVisible) status.Text = "Could not find configuration files: " + error.Message; }
        };
    }
    private void Filter()
    {
        var terms = (query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        results.ItemsSource = commands.Where(c => terms.All(t => (c.Title + " " + c.Detail).Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        results.SelectedIndex = results.ItemCount > 0 ? 0 : -1;
    }
    private void Choose() { if (results.SelectedItem is PaletteCommand command) { chosen = command; Close(); } }
}
