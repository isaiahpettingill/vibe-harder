using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CodexManager.Tests.TestApp))]

namespace CodexManager.Tests;
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
