using Avalonia.Controls;
using Avalonia.Input;

namespace CodexManager;

internal static class SettingsEdit
{
    internal static void OnCommit(TextBox field, Action save)
    {
        field.LostFocus += (_, _) => save();
        field.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || field.AcceptsReturn) return;
            save();
            args.Handled = true;
        };
    }

    internal static void OnCommit(NumericUpDown field, Action save)
    {
        // NumericUpDown changes Value as digits are typed. Avoid acting on
        // intermediate values when the edit restarts a listening server.
        field.ValueChanged += (_, _) => { if (!field.IsKeyboardFocusWithin) save(); };
        field.LostFocus += (_, _) => save();
        field.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter) save();
        }, handledEventsToo: true);
    }
}
