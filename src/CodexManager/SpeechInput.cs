namespace CodexManager;

// Speak to type. Each platform installs its own service: Android's speech recognizer, Windows
// voice typing, macOS dictation, or on Linux a local Whisper model downloaded on request.
public interface ISpeechInput
{
    // Ready to listen now; a downloadable model may still be missing.
    bool Ready { get; }
    // Listens until the speaker finishes, or until `stop` for recordings that need a second tap.
    // Returns the text to insert, or null when the system typed it into the focused field itself.
    Task<string?> Listen(CancellationToken stop);
    // True while Listen records and waits for the second tap.
    bool StopsOnTap { get; }
}

// A speech model that must be downloaded before Listen works.
public interface ISpeechModel
{
    string Description { get; }
    bool Downloaded { get; }
    Task Download(IProgress<double> progress, CancellationToken token);
    void Remove();
}

public static class SpeechInput
{
    public const string EnabledKey = "speechInput";
    public static ISpeechInput? Current { get; set; }
    public static bool Enabled { get; private set; } = true;
    public static event Action? Changed;
    public static bool Available => Enabled && Current?.Ready == true;
    public static void Apply(Store store) { Enabled = store.Setting(EnabledKey) != "0"; Changed?.Invoke(); }
    public static void Set(Store store, bool enabled) { store.Setting(EnabledKey, enabled ? "1" : "0"); Apply(store); }
    public static void Refresh() => Changed?.Invoke();

    // The text to insert at the caret: the dictation, separated from the draft by single spaces.
    public static string Insertion(string draft, int caret, string spoken)
    {
        spoken = spoken.Trim();
        caret = Math.Clamp(caret, 0, draft.Length);
        if (spoken.Length == 0) return "";
        var before = caret > 0 && !char.IsWhiteSpace(draft[caret - 1]) ? " " : "";
        var after = caret < draft.Length && !char.IsWhiteSpace(draft[caret]) ? " " : "";
        return before + spoken + after;
    }
}
