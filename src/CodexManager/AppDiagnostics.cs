using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace CodexManager;

public static class AppDiagnostics
{
    private static readonly object Gate = new();
    private static int installed;
    private static int recoveryQueued;
    public static event Func<Exception, Task>? RecoveryRequested;
    public static bool IsUnrecoverable(Exception error) => error is OutOfMemoryException or StackOverflowException or AccessViolationException or SEHException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsUnrecoverable)
        || error.InnerException is { } inner && IsUnrecoverable(inner);
    public static string DirectoryPath => Path.Combine(Store.DataDirectory, "logs");
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("Fatal process exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Record("Unobserved background task", e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI callback", e.Exception);
            if (IsUnrecoverable(e.Exception)) return;
            e.Handled = true;
            var error = e.Exception;
            // Cancellation can arrive after a view or native D-Bus watcher was closed.
            if (e.Exception is OperationCanceledException || Interlocked.Exchange(ref recoveryQueued, 1) != 0) return;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (RecoveryRequested is { } handlers)
                        foreach (var handler in handlers.GetInvocationList().Cast<Func<Exception, Task>>())
                            try { await handler(error); } catch (Exception failure) when (!IsUnrecoverable(failure)) { Record("UI recovery failed", failure); }
                }
                finally { Interlocked.Exchange(ref recoveryQueued, 0); }
            });
        };
    }
    public static string Message(string context, Exception error)
    {
        Record(context, error);
        return context + ": " + error.Message;
    }
    public static void Record(string context, Exception error, string? directory = null)
    {
        try
        {
            lock (Gate)
            {
                directory ??= DirectoryPath; Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 512 * 1024) File.Move(path, Path.Combine(directory, "errors.previous.log"), true);
                var detail = error.ToString(); if (detail.Length > 16 * 1024) detail = detail[..(16 * 1024)] + "\n[truncated]";
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} · {context} · {RuntimeInformation.OSDescription} · .NET {Environment.Version}\n{detail}\n\n");
            }
        }
        catch { /* Diagnostics must never turn an existing error into another crash. */ }
    }
}
