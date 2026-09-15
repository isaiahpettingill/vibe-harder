using Avalonia.Threading;
using Porta.Pty;
using SvcSystems.UI.Terminal;

namespace CodexManager;

public sealed class TerminalSession : IDisposable
{
    public TerminalControlModel Model { get; } = new(new TerminalOptions { Cols = 100, Rows = 24, ReflowOnResize = false });
    private IPtyConnection? connection;
    private DurableTerminalConnection? durable;
    public string? DurableId { get; private set; }
    private readonly CancellationTokenSource lifetime = new();
    private bool exited;
    private bool disposed;
    private bool completionReported;
    private (int Cols, int Rows) ptySize;
    private readonly System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();
    public event Action? Completed;
    public event Action? OutputChanged;
    public event Action<string>? RawOutput;
    public void Input(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));
    public void Resize(int cols, int rows) => Model.Resize(Math.Clamp(cols, 2, 500), Math.Clamp(rows, 2, 200), 1, 1);
    public TerminalOutput Output { get; } = new();
    public async Task Start(Workspace workspace, string? command = null, Store? settings = null, string? durableId = null, bool resumeOnly = false)
    {
        if (durableId is not null && command is null)
        {
            DurableId = durableId;
            durable = new DurableTerminalConnection(settings?.DirectoryPath ?? Store.DataDirectory, durableId, Model,
                text => { Model.Feed(text); RawOutput?.Invoke(text); Output.Append(System.Text.Encoding.UTF8.GetBytes(text)); OutputChanged?.Invoke(); },
                () => { exited = true; Completed?.Invoke(); });
            await durable.Start(workspace, resumeOnly);
            Model.UserInput += (_, e) => Write(e.Data.Span);
            return;
        }
        var options = Options(workspace, command);
        if (command is null && settings is not null) TerminalPreferences.Apply(options, workspace, settings);
        options.Cols = Math.Max(2, Model.Terminal.Cols); options.Rows = Math.Max(2, Model.Terminal.Rows);
        connection = await PtyProvider.SpawnAsync(options, lifetime.Token);
        ptySize = (options.Cols, options.Rows);
        connection.ProcessExited += (_, _) => exited = true;
        Model.UserInput += (_, e) => Write(e.Data.Span);
        // Cursor/device queries are protocol replies, not keyboard input. Without
        // this bridge ConPTY and shells cannot reliably redraw their current line.
        Model.Terminal.Engine.DataReceived += (_, e) => Write(System.Text.Encoding.UTF8.GetBytes(e.Data));
        Model.SizeChanged += (_, _) => ResizePty();
        ResizePty();
        _ = Pump();
    }
    private void Write(ReadOnlySpan<byte> data)
    {
        if (exited || disposed) return;
        if (durable is not null) { if (!disposed) durable.Input(System.Text.Encoding.UTF8.GetString(data)); return; }
        if (exited || disposed || connection is null) return;
        try { connection.WriterStream.Write(data); connection.WriterStream.Flush(); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { exited = true; Dispatcher.UIThread.Post(ReportCompletion); }
    }
    private void ResizePty()
    {
        if (exited || disposed || connection is null) return;
        var size = (Math.Max(2, Model.Terminal.Cols), Math.Max(2, Model.Terminal.Rows));
        if (size == ptySize) return; // Pixel-only layout changes must not signal the shell.
        try { connection.Resize(size.Item1, size.Item2); ptySize = size; }
        catch (Exception error) when (error is IOException or InvalidOperationException) { exited = true; Dispatcher.UIThread.Post(ReportCompletion); }
    }
    public static PtyOptions Options(Workspace workspace, string? command = null)
    {
        var options = new PtyOptions { Name = "xterm-256color", Cols = 100, Rows = 24, Cwd = workspace.IsWsl ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workspace.Path };
        options.Environment["TERM"] = "xterm-256color";
        options.Environment["COLORTERM"] = "truecolor";
        if (workspace.IsWsl)
        {
            var forwarded = (Environment.GetEnvironmentVariable("WSLENV") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Where(v => v.Split('/')[0] is not ("TERM" or "COLORTERM"));
            options.Environment["WSLENV"] = string.Join(':', forwarded.Concat(["TERM/u", "COLORTERM/u"]));
            options.App = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            // wsl.exe treats quoted switches as a Linux command. Porta otherwise quotes every argument.
            options.VerbatimCommandLine = true;
            options.CommandLine = ["--distribution", Hosts.WindowsArgument(workspace.Distro!), "--cd", Hosts.WindowsArgument(workspace.Path)];
            if (command is not null) options.CommandLine = [.. options.CommandLine, "--exec", "bash", "-lc", Hosts.WindowsArgument("exec " + command)];
        }
        else if (OperatingSystem.IsWindows())
        { options.App = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"); options.CommandLine = ["-NoLogo"]; }
        else
        {
            options.App = "/bin/sh";
            options.CommandLine = ["-lc", Hosts.LocalShellCommand("selected_shell=${SHELL:-}; if test -z \"$selected_shell\"; then selected_shell=$(getent passwd \"$(id -u)\" 2>/dev/null | cut -d: -f7); fi; if test -z \"$selected_shell\" && command -v dscl >/dev/null; then selected_shell=$(dscl . -read \"/Users/$(id -un)\" UserShell | sed 's/^UserShell: //'); fi; exec \"${selected_shell:-/bin/sh}\" -i")];
        }
        if (command is not null && !workspace.IsWsl)
            options.CommandLine = OperatingSystem.IsWindows() ? ["-NoLogo", "-NoProfile", "-Command", Hosts.WindowsShellCommand(command)] : ["-lc", Hosts.LocalShellCommand(command)];
        return options;
    }
    private async Task Pump()
    {
        try
        {
            var buffer = new byte[8192];
            while (await connection!.ReaderStream.ReadAsync(buffer, lifetime.Token) is var read && read > 0)
            {
                var copy = buffer[..read];
                var chars = new char[System.Text.Encoding.UTF8.GetMaxCharCount(read)];
                var count = decoder.GetChars(copy, chars, false);
                var text = new string(chars, 0, count);
                await Dispatcher.UIThread.InvokeAsync(() => { Model.Feed(text); RawOutput?.Invoke(text); Output.Append(copy); OutputChanged?.Invoke(); });
            }
            await Dispatcher.UIThread.InvokeAsync(() => { if (!disposed) { Model.Feed("\r\n[Shell exited]\r\n"); RawOutput?.Invoke("\r\n[Shell exited]\r\n"); } });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!lifetime.IsCancellationRequested) await Dispatcher.UIThread.InvokeAsync(() => Model.Feed("\r\n" + error.Message)); }
        finally { exited = true; await Dispatcher.UIThread.InvokeAsync(ReportCompletion); }
    }
    private void ReportCompletion()
    {
        if (disposed || completionReported) return;
        completionReported = true; Completed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel();
        durable?.Dispose();
        try { connection?.Kill(); } catch (InvalidOperationException) { }
        connection?.Dispose();
    }
    public async Task Close()
    {
        if (durable is not null) await durable.Close();
        Dispose();
    }
}
