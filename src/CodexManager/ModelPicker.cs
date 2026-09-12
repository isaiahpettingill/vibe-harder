using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace CodexManager;

public static class ModelPicker
{
    public static bool IsModel(SessionConfig option) => option.Kind == "model" || option.Id.Contains("model", StringComparison.OrdinalIgnoreCase);
    public static string[] Recent(Store store, AgentProvider provider) => JsonNode.Parse(store.Setting("recentModels:" + provider) ?? "[]")!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
    public static void Remember(Store store, AgentProvider provider, string value) => store.Setting("recentModels:" + provider,
        new JsonArray(new[] { value }.Concat(Recent(store, provider)).Distinct().Take(12).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()).ToJsonString());

    public static Flyout Create(SessionConfig option, IReadOnlyList<string> recent, Func<string, Task> choose)
    {
        var search = new TextBox { Name = "ModelSearch", PlaceholderText = "Search models…" };
        var list = new ListBox { Name = "ModelResults", MaxHeight = 320 };
        var panel = new StackPanel { Width = 300, Spacing = 8, Children = { search, list } };
        var flyout = new Flyout { Content = panel };
        void Filter()
        {
            var query = search.Text?.Trim() ?? "";
            list.ItemsSource = option.Values.Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || v.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => { var i = recent.ToList().IndexOf(v.Value); return i < 0 ? int.MaxValue : i; }).ToArray();
        }
        async Task Select()
        {
            if (list.SelectedItem is not SessionValue value) return;
            flyout.Hide(); await choose(value.Value);
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) => { if (e.Key == Key.Down && list.ItemCount > 0) { list.SelectedIndex = 0; list.Focus(); e.Handled = true; } };
        list.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Select(); } };
        list.Tapped += async (_, _) => await Select();
        flyout.Opened += (_, _) => { search.Text = ""; Filter(); search.Focus(); };
        Filter(); return flyout;
    }
}
