using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class CostHistoryView : Control
{
    public IReadOnlyList<(DateOnly Date, decimal Cost)> Points { get; init; } = [];
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var maximum = Points.Count > 0 ? Points.Max(p => p.Cost) : 0;
        if (maximum <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var pen = new Pen(Brushes.MediumPurple, 2);
        var first = Points[0].Date; var days = Math.Max(1, Points[^1].Date.DayNumber - first.DayNumber);
        Point? previous = null;
        foreach (var point in Points)
        {
            var location = new Point(6 + (Bounds.Width - 12) * (point.Date.DayNumber - first.DayNumber) / days,
                Bounds.Height - 6 - (Bounds.Height - 12) * (double)(point.Cost / maximum));
            if (previous is { } start) context.DrawLine(pen, start, location);
            context.DrawEllipse(Brushes.MediumPurple, null, location, 3, 3);
            previous = location;
        }
    }
    public static Control Create(Store store)
    {
        var host = new ContentControl();
        string? signature = null;
        string? currency = null;
        void Update()
        {
            var value = store.Setting("usage:daily");
            if (host.Content is not null && value == signature) return;
            signature = value;
            host.Content = Build(store, currency, selected => currency = selected);
        }
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Update();
        host.AttachedToVisualTree += (_, _) => { Update(); timer.Start(); };
        host.DetachedFromVisualTree += (_, _) => timer.Stop();
        Update();
        return host;
    }
    private static Control Build(Store store, string? preferredCurrency, Action<string> selected)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Reported daily cost", FontSize = 18 });
        var currencies = ChatUsage.Currencies(store);
        if (currencies.Length == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No cost history yet.", Classes = { "muted" } }); return panel;
        }
        var picker = new ComboBox { ItemsSource = currencies, SelectedItem = preferredCurrency is not null && currencies.Contains(preferredCurrency) ? preferredCurrency : currencies.Contains("USD") ? "USD" : currencies[0] };
        var chartHost = new ContentControl(); var details = new StackPanel { Spacing = 4 };
        void Refresh()
        {
            var currency = picker.SelectedItem as string ?? currencies[0];
            selected(currency);
            var points = ChatUsage.Daily(store, currency);
            chartHost.Content = new CostHistoryView { Height = 180, Points = points, IsHitTestVisible = false };
            details.Children.Clear();
            if (points.Count > 0)
            {
                details.Children.Add(new TextBlock { Text = points[0].Date.ToString("yyyy-MM-dd") + " — " + points[^1].Date.ToString("yyyy-MM-dd"), Classes = { "muted" } });
                details.Children.Add(new TextBlock { Text = "Total: " + ChatUsage.Money(currency, points.Sum(p => p.Cost)) });
                foreach (var point in points.Reverse().Take(30))
                    details.Children.Add(new TextBlock { Text = point.Date.ToString("yyyy-MM-dd") + "   " + ChatUsage.Money(currency, point.Cost) });
            }
        }
        picker.SelectionChanged += (_, _) => Refresh();
        panel.Children.Add(picker); panel.Children.Add(chartHost); panel.Children.Add(details); Refresh();
        return panel;
    }
}
