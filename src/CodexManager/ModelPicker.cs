using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;

namespace CodexManager;

public static class ModelPicker
{
    public static bool IsModel(SessionConfig option) => option.Kind == "model" || option.Id.Contains("model", StringComparison.OrdinalIgnoreCase);
    public static string[] Recent(Store store, AgentProvider provider)
    {
        try
        {
            return JsonNode.Parse(store.Setting("recentModels:" + provider) ?? "[]") is JsonArray values
                ? values.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var text) ? text : null).OfType<string>()
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(12).ToArray() : [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
    public static void Remember(Store store, AgentProvider provider, string value) => store.Setting("recentModels:" + provider,
        new JsonArray(new[] { value }.Concat(Recent(store, provider)).Distinct().Take(12).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()).ToJsonString());

    public static Flyout Create(SessionConfig option, IReadOnlyList<string> recent, Func<string, Task> choose)
    {
        var search = new TextBox { Name = "ModelSearch", PlaceholderText = "Search models…" };
        var list = new ListBox { Name = "ModelResults", MaxHeight = 320, ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        var empty = new TextBlock { Text = "Search to find a model.", IsVisible = false };
        var panel = new StackPanel { Width = 300, Spacing = 8, Children = { search, list, empty } };
        var flyout = new Flyout { Content = panel };
        var recentIds = recent.Distinct().ToList();
        void Filter()
        {
            var query = search.Text?.Trim() ?? "";
            var values = query.Length == 0
                ? recentIds.Select(id => option.Values.FirstOrDefault(v => v.Value == id)).OfType<SessionValue>().Take(5)
                : option.Values.Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || v.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(v => { var i = recentIds.IndexOf(v.Value); return i < 0 ? int.MaxValue : i; });
            list.ItemsSource = values.ToArray();
            empty.Text = query.Length == 0 ? "Search to find a model." : "No matching models.";
            empty.IsVisible = list.ItemCount == 0;
        }
        async Task Select()
        {
            if (list.SelectedItem is not SessionValue value) return;
            flyout.Hide(); await choose(value.Value);
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += async (_, e) =>
        {
            if (list.ItemCount == 0) return;
            if (e.Key == Key.Down) { list.SelectedIndex = 0; list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter) { if (list.SelectedIndex < 0) list.SelectedIndex = 0; e.Handled = true; await Select(); }
        };
        list.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Select(); } };
        list.Tapped += async (_, _) => await Select();
        flyout.Opened += (_, _) => { search.Text = ""; Filter(); search.Focus(); };
        Filter(); return flyout;
    }
}
