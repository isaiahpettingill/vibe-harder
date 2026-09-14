using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using TerminalKey = XTerm.Input.Key;
using TerminalModifiers = XTerm.Input.KeyModifiers;

namespace CodexManager.Android;

// A raw IME endpoint, not a line editor. The terminal itself owns all editing,
// echo, cursor movement and history; Android only supplies committed keystrokes.
internal sealed class AndroidTerminalKeyboard : Java.Lang.Object, IMobileTerminalKeyboard
{
    private readonly MainActivity activity;
    private readonly InputView view;
    private readonly InputMethodManager manager;
    private Action<TerminalKeystroke>? receive;
    private int generation;
    public AndroidTerminalKeyboard(MainActivity activity)
    {
        this.activity = activity;
        manager = (InputMethodManager)activity.GetSystemService(Context.InputMethodService)!;
        view = new InputView(this, activity) { Focusable = true, FocusableInTouchMode = true, Alpha = 0, ImportantForAccessibility = ImportantForAccessibility.No };
        activity.AddContentView(view, new ViewGroup.LayoutParams(1, 1));
    }
    public void Focus(Action<TerminalKeystroke> receiver, bool showKeyboard)
    {
        if (receive != receiver) { receive = receiver; ++generation; }
        var version = generation;
        // Avalonia finishes its own focus handling after a toolbar tap. Restore
        // the raw IME endpoint after that event, otherwise modifiers are bypassed.
        view.Post(() =>
        {
            if (version != generation || receive != receiver) return;
            view.RequestFocus(); manager.RestartInput(view);
            if (showKeyboard) manager.ShowSoftInput(view, ShowFlags.Implicit);
        });
    }
    public void Release(Action<TerminalKeystroke> receiver)
    {
        if (receive != receiver) return;
        receive = null; ++generation;
        manager.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        view.ClearFocus();
    }
    private void Send(int version, TerminalKeystroke input) => activity.RunOnUiThread(() => { if (version == generation) receive?.Invoke(input); });

    private bool SendKey(KeyEvent e, int version)
    {
        if (e.KeyCode == Keycode.Back) return false;
        if (e.Action == KeyEventActions.Up) return true;
        if (e.Action != KeyEventActions.Down) return false;
        var modifiers = (e.IsCtrlPressed ? TerminalModifiers.Control : 0) | (e.IsAltPressed ? TerminalModifiers.Alt : 0) | (e.IsShiftPressed ? TerminalModifiers.Shift : 0);
        TerminalKey? key = e.KeyCode switch
        {
            Keycode.Enter or Keycode.NumpadEnter => TerminalKey.Enter,
            Keycode.Del => TerminalKey.Backspace, Keycode.ForwardDel => TerminalKey.Delete,
            Keycode.Tab => TerminalKey.Tab, Keycode.Escape => TerminalKey.Escape,
            Keycode.DpadUp => TerminalKey.UpArrow, Keycode.DpadDown => TerminalKey.DownArrow,
            Keycode.DpadLeft => TerminalKey.LeftArrow, Keycode.DpadRight => TerminalKey.RightArrow,
            Keycode.MoveHome => TerminalKey.Home, Keycode.MoveEnd => TerminalKey.End,
            Keycode.PageUp => TerminalKey.PageUp, Keycode.PageDown => TerminalKey.PageDown,
            Keycode.Insert => TerminalKey.Insert,
            >= Keycode.F1 and <= Keycode.F12 => TerminalKey.F1 + (e.KeyCode - Keycode.F1),
            _ => null
        };
        if (key is not null) { Send(version, new(Key: key, Modifiers: modifiers)); return true; }
        // Ctrl/Alt are encoded by the terminal engine, not by Android's keymap.
        var codepoint = e.GetUnicodeChar(e.MetaState & ~(MetaKeyStates.CtrlMask | MetaKeyStates.AltMask | MetaKeyStates.MetaMask));
        if (codepoint is > 0 and <= 0x10ffff) { Send(version, new(char.ConvertFromUtf32(codepoint), Modifiers: modifiers)); return true; }
        return false;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (receive is { } receiver) Release(receiver);
            if (ReferenceEquals(MobileTerminalKeyboard.Current, this)) MobileTerminalKeyboard.Current = null;
            if (view.Parent is ViewGroup parent) parent.RemoveView(view);
            view.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed class InputView(AndroidTerminalKeyboard owner, Context context) : View(context)
    {
        public override bool OnCheckIsTextEditor() => true;
        public override IInputConnection? OnCreateInputConnection(EditorInfo? info)
        {
            if (info is null || owner.receive is null) return null;
            info.InputType = InputTypes.ClassText | InputTypes.TextVariationVisiblePassword | InputTypes.TextFlagNoSuggestions;
            info.ImeOptions = ImeFlags.NoFullscreen | ImeFlags.NoExtractUi | ImeFlags.NoEnterAction;
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) info.ImeOptions |= ImeFlags.NoPersonalizedLearning;
            info.InitialSelStart = info.InitialSelEnd = 0;
            return new Connection(owner, this, owner.generation);
        }
        public override bool OnKeyDown(Keycode keyCode, KeyEvent? e) => e is not null && owner.SendKey(e, owner.generation) || base.OnKeyDown(keyCode, e);
        public override bool OnKeyUp(Keycode keyCode, KeyEvent? e) => e is not null && owner.SendKey(e, owner.generation) || base.OnKeyUp(keyCode, e);
    }
    private sealed class Connection(AndroidTerminalKeyboard owner, View view, int version) : BaseInputConnection(view, false)
    {
        private string composing = "";
        public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            composing = ""; owner.Send(version, new(text?.ToString()?.Replace("\n", "\r"))); return true;
        }
        public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition) { composing = text?.ToString() ?? ""; return true; }
        public override bool FinishComposingText()
        {
            if (composing.Length > 0) owner.Send(version, new(composing));
            composing = ""; return true;
        }
        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            if (composing.Length > 0) { composing = composing[..Math.Max(0, composing.Length - Math.Max(0, beforeLength))]; return true; }
            for (var i = 0; i < Math.Clamp(beforeLength, 0, 4096); i++) owner.Send(version, new(Key: TerminalKey.Backspace));
            for (var i = 0; i < Math.Clamp(afterLength, 0, 4096); i++) owner.Send(version, new(Key: TerminalKey.Delete));
            return true;
        }
        public override bool DeleteSurroundingTextInCodePoints(int beforeLength, int afterLength) => DeleteSurroundingText(beforeLength, afterLength);
        public override bool SendKeyEvent(KeyEvent? e) => e is not null && owner.SendKey(e, version);
        public override bool PerformEditorAction(ImeAction actionCode) { FinishComposingText(); owner.Send(version, new(Key: TerminalKey.Enter)); return true; }
        public override Java.Lang.ICharSequence? GetTextBeforeCursorFormatted(int length, GetTextFlags flags) => new Java.Lang.String(composing);
        public override Java.Lang.ICharSequence? GetTextAfterCursorFormatted(int length, GetTextFlags flags) => new Java.Lang.String("");
        public override Java.Lang.ICharSequence? GetSelectedTextFormatted(GetTextFlags flags) => new Java.Lang.String("");
        public override void CloseConnection() { composing = ""; base.CloseConnection(); }
    }
}
