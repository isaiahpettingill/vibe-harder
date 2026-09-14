using Avalonia;
using Avalonia.Headless;

namespace CodexManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--terminal-broker", var profile])
        {
            using var owner = new AppInstance(Path.Combine(profile, "terminal-owner"));
            if (!owner.IsOwner) return;
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var hangup = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, context => context.Cancel = true);
            AppBuilder.Configure<App>().UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            using var stopped = new CancellationTokenSource();
            _ = TerminalBroker.Serve(profile, stopped.Token).ContinueWith(_ => stopped.Cancel(), TaskScheduler.Default);
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(stopped.Token);
            return;
        }
        string? directory;
        try { directory = args.Contains("--headless") ? null : WorkspaceLaunch.Parse(args); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; return; }
        using var instance = new AppInstance(Store.DataDirectory);
        if (!instance.IsOwner)
        {
            if (!instance.ActivateExisting(directory).GetAwaiter().GetResult()) { Console.Error.WriteLine("Could not reach the running Vibe Harder app. Try again."); Environment.ExitCode = 1; }
            return;
        }
        var pending = new Queue<string?>();
        if (directory is not null) pending.Enqueue(directory);
        void Deliver()
        {
            if ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow window) return;
            while (pending.TryDequeue(out var folder)) { window.ShowFromTray(); if (folder is not null) window.View.OpenLocalDirectory(folder); }
        }
        App.DesktopReady = Deliver;
        // Bind the dispatcher on the main thread before a pipe request can
        // arrive; first access from the listener would claim the wrong thread.
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        _ = instance.Listen(folder => dispatcher.Post(() => { pending.Enqueue(folder); Deliver(); }));
        if (Environment.GetEnvironmentVariable("CODEX_MANAGER_TRACE") == "1")
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        if (args.Contains("--headless")) { HeadlessHost.Run(args); return; }
        void Interrupt() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow window) window.RequestExit();
        });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Interrupt(); };
        using var termination = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; Interrupt(); });
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = "CodexManager" }).LogToTrace();
        // Keep KDE's mature X11/XWayland integration when available; also support
        // native Wayland-only sessions and an explicit native-backend preference.
        var backend = Environment.GetEnvironmentVariable("VIBE_HARDER_LINUX_BACKEND");
        if (OperatingSystem.IsLinux() && (backend == "wayland" || backend != "x11" && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))))
            builder = builder.UseWayland();
        return builder;
    }
}
