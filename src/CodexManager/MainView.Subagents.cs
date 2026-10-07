using Avalonia.Controls;

namespace CodexManager;

public partial class MainView
{
    private SubagentInspector? subagentInspector;
    private void CloseSubagent()
    {
        if (subagentInspector is null) return;
        subagentInspector.Dispose(); RootPanes.Children.Remove(subagentInspector); subagentInspector = null;
    }
    public void ShowSubagent(Message root, string[] path)
    {
        void Close() => CloseSubagent();
        Close();
        // Fills the chat pane only; the sidebar stays usable beside it.
        subagentInspector = new SubagentInspector(root, path, Close) { ZIndex = 1000 };
        Grid.SetRow(subagentInspector, 1); Grid.SetColumn(subagentInspector, 2);
        RootPanes.Children.Add(subagentInspector); subagentInspector.Focus();
    }
}
