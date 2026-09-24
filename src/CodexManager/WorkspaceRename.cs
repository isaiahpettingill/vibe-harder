using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace CodexManager;

public static class WorkspaceRename
{
    public static void Show(Control anchor, string currentName, Func<string, Task<bool>> rename)
    {
        var input = new TextBox { Name = "WorkspaceNameInput", Text = currentName, MinWidth = 200, MaxLength = 250 };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 280 };
        var save = new IconButton { Name = "SaveWorkspaceName", Icon = "send", Label = "Save workspace name" };
        var popup = new Flyout { Content = new StackPanel { Spacing = 6, Children = { input, error, save } } };
        async Task Apply()
        {
            if (!save.IsEnabled) return;
            var name = input.Text?.Trim() ?? "";
            if (name.Length == 0) { error.Text = "Enter a workspace name."; return; }
            save.IsEnabled = false;
            try
            {
                if (await rename(name)) popup.Hide();
                else error.Text = "Could not rename workspace. Check the connection and try again.";
            }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { save.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Apply();
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await Apply(); }
            else if (e.Key == Key.Escape) { e.Handled = true; popup.Hide(); }
        };
        popup.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        FlyoutBase.SetAttachedFlyout(anchor, popup);
        popup.ShowAt(anchor);
    }
}
