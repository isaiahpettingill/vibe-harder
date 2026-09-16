using Avalonia.Controls;

namespace CodexManager;

public partial class MainView
{
    private SubagentInspector? subagentInspector;
    public void ShowSubagent(Message root, string[] path)
    {
        void Close()
        {
            if (subagentInspector is null) return;
            subagentInspector.Dispose(); RootPanes.Children.Remove(subagentInspector); subagentInspector = null;
        }
        Close();
        subagentInspector = new SubagentInspector(root, path, Close) { ZIndex = 1000 };
        Grid.SetColumnSpan(subagentInspector, RootPanes.ColumnDefinitions.Count); Grid.SetRowSpan(subagentInspector, RootPanes.RowDefinitions.Count);
        RootPanes.Children.Add(subagentInspector); subagentInspector.Focus();
    }
}
