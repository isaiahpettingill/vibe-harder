using Avalonia;

namespace CodexManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new AppInstance(Store.DataDirectory);
        if (!instance.IsOwner) { instance.ActivateExisting().GetAwaiter().GetResult(); return; }
        _ = instance.Listen(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow window) window.ShowFromTray();
        }));
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
