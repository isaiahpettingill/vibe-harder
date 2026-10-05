using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CodexManager.Tests.TestApp))]

namespace CodexManager.Tests;
public static class TestApp
{
    // Hundreds of parallel tests block on processes, pipes and waits. Without headroom the
    // pool's slow thread injection delays timers by seconds and timing assertions race.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void ReserveThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
