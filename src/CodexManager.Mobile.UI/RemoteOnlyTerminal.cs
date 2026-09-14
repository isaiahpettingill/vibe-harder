using SvcSystems.UI.Terminal;

namespace CodexManager;

// The shared shell UI can refer to a local terminal, but mobile only creates
// RemoteTerminalView. Keep the native PTY provider out of the Android graph.
public sealed class TerminalSession : IDisposable
{
    public TerminalControlModel Model { get; } = new(new TerminalOptions { Cols = 100, Rows = 24 });
    public TerminalOutput Output { get; } = new();
    public event Action? Completed { add { } remove { } }
    public event Action? OutputChanged { add { } remove { } }
    public event Action<string>? RawOutput { add { } remove { } }
    public string? DurableId => null;
    public Task Start(Workspace workspace, string? command = null, Store? settings = null, string? durableId = null, bool resumeOnly = false) => Task.FromException(new PlatformNotSupportedException("Use the remote terminal on Android."));
    public Task Close() => Task.CompletedTask;
    public void Input(string text) => throw new PlatformNotSupportedException("Use the remote terminal on Android.");
    public void Resize(int cols, int rows) { }
    public void Dispose() { }
}
