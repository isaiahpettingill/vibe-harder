using Avalonia.Controls;

namespace CodexManager;

public partial class MainView
{
    private string? terminalPaneWorkspace;
    private readonly Dictionary<string, (bool Visible, object? Selected, double Width)> terminalPaneStates = [];

    private void SwitchTerminalWorkspace(string id)
    {
        if (terminalPaneWorkspace == id) return;
        if (terminalPaneWorkspace is { } previous)
            terminalPaneStates[previous] = (TerminalDrawer.IsVisible, TerminalTabs.SelectedItem, TerminalDrawer.Width);
        if (terminalPaneWorkspace is { } hidden && remoteTerminalTabs.TryGetValue(hidden, out var hiddenTab)) (hiddenTab.Content as RemoteTerminalView)?.SetVisible(false);
        terminalPaneWorkspace = id;
        var tabs = terminals.GetValueOrDefault(id)?.Select(t => t.Tab).ToArray() ?? (remoteTerminalTabs.TryGetValue(id, out var remoteTab) ? [remoteTab] : []);
        TerminalTabs.ItemsSource = tabs;
        if (terminalPaneStates.TryGetValue(id, out var state))
        {
            TerminalTabs.SelectedItem = tabs.FirstOrDefault(t => ReferenceEquals(t, state.Selected)) ?? tabs.FirstOrDefault();
            TerminalDrawer.Width = state.Width;
            TerminalDrawer.IsVisible = state.Visible;
        }
        else { TerminalTabs.SelectedItem = tabs.FirstOrDefault(); TerminalDrawer.IsVisible = false; }
    }
}
