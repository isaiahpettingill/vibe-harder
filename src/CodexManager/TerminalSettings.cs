using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CodexManager;

public sealed class TerminalSettings : Window
{
    public TerminalSettings(Store store, IEnumerable<Workspace> workspaces)
    {
        Title = "Terminal settings"; Width = 530; Height = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var platforms = new[] { OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux" }
            .Concat(workspaces.Where(w => w.IsWsl).Select(TerminalPreferences.Platform)).Distinct().ToArray();
        var platform = new ComboBox { Name = "TerminalPlatform", ItemsSource = platforms, HorizontalAlignment = HorizontalAlignment.Stretch };
        var shells = new ComboBox { Name = "TerminalShell", HorizontalAlignment = HorizontalAlignment.Stretch };
        var command = new TextBox { Name = "TerminalCommand", PlaceholderText = "Leave blank to use the system shell" };
        var hint = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Name = "SaveTerminalSettings", Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10, Children = { platform, shells, command, hint, error, save } }; Content = panel;
        void ShowCommand() => command.IsVisible = (string?)platform.SelectedItem != "windows" || (shells.SelectedItem as ShellChoice)?.Id == "custom";
        platform.SelectionChanged += (_, _) =>
        {
            var key = (string)platform.SelectedItem!;
            shells.IsVisible = key == "windows";
            if (shells.IsVisible)
            {
                var choices = TerminalPreferences.WindowsShells().Concat(new[] { new ShellChoice("custom", "Custom command (via cmd.exe)", "", []) }).ToArray();
                shells.ItemsSource = choices;
                var selected = store.Setting("terminalShell:windows");
                shells.SelectedItem = choices.FirstOrDefault(s => s.Id == selected) ?? choices[0];
                error.Text = selected is not null && !choices.Any(s => s.Id == selected) ? "Previously selected shell is unavailable. Choose a replacement." : "";
            }
            command.Text = store.Setting("terminalCommand:" + key) ?? "";
            hint.Text = key == "windows" ? "Only installed shells are listed. Custom commands run through cmd.exe /d /s /c. Changes apply to new terminal tabs." : "Leave the command blank to use this environment’s default shell. Custom commands run through sh -lc. Changes apply to new terminal tabs.";
            ShowCommand();
        };
        shells.SelectionChanged += (_, _) => ShowCommand();
        save.Click += (_, _) =>
        {
            var key = (string)platform.SelectedItem!;
            if (key == "windows")
            {
                var selected = (ShellChoice)shells.SelectedItem!;
                if (selected.Id == "custom" && string.IsNullOrWhiteSpace(command.Text)) { error.Text = "Enter a command to start your shell."; return; }
                store.Setting("terminalShell:windows", selected.Id);
            }
            store.Setting("terminalCommand:" + key, command.Text ?? ""); Close();
        };
        platform.SelectedIndex = 0;
    }
}
