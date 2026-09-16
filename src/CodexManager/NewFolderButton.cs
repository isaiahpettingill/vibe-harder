using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public sealed class NewFolderButton : Button
{
    public NewFolderButton(Func<string, Task> create)
    {
        Name = "NewFolderButton"; Content = AppIcons.Label("new-folder", "New folder");
        var name = new TextBox { Name = "NewFolderName", PlaceholderText = "Folder name", MinWidth = 200, MaxLength = 255 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        var submit = new Button { Name = "CreateFolderButton", Content = "Create", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };
        var popup = new Flyout { Content = new StackPanel { Spacing = 8, Children = { name, error, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, submit } } } } };
        Flyout = popup;
        popup.Opened += (_, _) => { error.Text = ""; name.Focus(); name.SelectAll(); };
        cancel.Click += (_, _) => popup.Hide();
        async Task Create()
        {
            if (!submit.IsEnabled) return;
            submit.IsEnabled = false; name.IsEnabled = false; error.Text = "";
            try { await create(name.Text ?? ""); name.Text = ""; popup.Hide(); }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { submit.IsEnabled = true; name.IsEnabled = true; }
        }
        submit.Click += async (_, _) => await Create();
        name.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Create(); } else if (e.Key == Key.Escape) { e.Handled = true; popup.Hide(); } };
        DetachedFromVisualTree += (_, _) => popup.Hide();
    }
}
