using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace CodexManager;

public sealed class SubagentInspector : Grid, IDisposable
{
    public Message Root { get; }
    public string[] Path { get; }
    private readonly SubagentView transcript = new() { InspectorMode = true };
    private readonly ScrollViewer scroll;
    private bool disposed;
    public SubagentInspector(Message root, string[] path, Action close)
    {
        Root = root; Path = path; Name = "SubagentInspector"; Focusable = true;
        RowDefinitions = new("Auto,*");
        this.Bind(BackgroundProperty, this.GetResourceObservable("AppBackground"));
        var back = new Button { Name = "CloseSubagentInspector", Content = AppIcons.Label("chevron-left", "Back to chat") }; back.Click += (_, _) => close();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(14, 10), Children = { back, new TextBlock { Text = "SUBAGENT · Read-only", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } } } };
        Children.Add(header);
        scroll = new ScrollViewer { Content = transcript, Margin = new Thickness(20, 8), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); Children.Add(scroll);
        root.PropertyChanged += RootChanged;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; close(); } };
        DetachedFromVisualTree += (_, _) => Dispose();
        Refresh();
    }
    private void RootChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (disposed) return;
        var follow = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 80;
        var info = Root.Subagent ?? SubagentInfo.FromLegacy(Root.Text);
        foreach (var id in Path) info = info?.Activity.FirstOrDefault(e => e.Id == id)?.Child;
        if (info is not null) { transcript.Update(info); transcript.Expand(); }
        if (follow) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!disposed) scroll.ScrollToEnd(); }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
    public void Dispose() { disposed = true; Root.PropertyChanged -= RootChanged; }
}
