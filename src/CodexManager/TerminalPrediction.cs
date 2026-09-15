namespace CodexManager;

// Predictions never enter the terminal model or its scrollback. Require a real
// echo on each input line before displaying speculative characters.
public sealed class TerminalPrediction
{
    public string Pending { get; private set; } = "";
    public bool Confirmed { get; private set; }
    public int Column { get; private set; }
    public int Row { get; private set; }
    private DateTimeOffset expires;
    public string Visible => Confirmed ? Pending : "";
    public void Reset() { Pending = ""; Confirmed = false; }
    public void Input(string text, int column, int row, int columns, bool eligible)
    {
        if (!eligible || text.Length == 0 || text.Any(c => c < ' ' || c > '~') || column + Pending.Length + text.Length >= columns)
        { Reset(); return; }
        if (Pending.Length == 0) { Column = column; Row = row; }
        Pending += text; expires = DateTimeOffset.UtcNow.AddMilliseconds(750);
    }
    public void Reconcile(int column, int row, Func<int, string> cell, bool eligible)
    {
        if (!eligible || DateTimeOffset.UtcNow > expires) { Reset(); return; }
        if (Pending.Length == 0) return;
        var count = column - Column;
        if (row != Row || count < 0 || count > Pending.Length) { Reset(); return; }
        for (var i = 0; i < count; i++) if (cell(Column + i) != Pending[i].ToString()) { Reset(); return; }
        if (count == 0) return;
        Confirmed = true; Pending = Pending[count..]; Column = column;
    }
}
