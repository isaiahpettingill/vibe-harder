using XTerm.Input;

namespace CodexManager;

public readonly record struct TerminalKeystroke(string? Text = null, Key? Key = null, KeyModifiers Modifiers = KeyModifiers.None);

public interface IMobileTerminalKeyboard
{
    void Focus(Action<TerminalKeystroke> receive, bool showKeyboard);
    void Release(Action<TerminalKeystroke> receive);
}

public static class MobileTerminalKeyboard
{
    public static IMobileTerminalKeyboard? Current { get; set; }
}
