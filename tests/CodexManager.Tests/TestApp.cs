using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CodexManager.Tests.TestApp))]

namespace CodexManager.Tests;
public static class TestApp
{
    public static readonly List<(string Id, string Chat, string Title, Action Open)> Notifications = [];
    // Hundreds of parallel tests block on processes, pipes and waits. Without headroom the
    // pool's slow thread injection delays timers by seconds and timing assertions race.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void ReserveThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
        // Record desktop notifications instead of showing them.
        PermissionNotifications.DesktopOverride = (id, chat, open, title) => { lock (Notifications) Notifications.Add((id, chat, title, open)); };
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
