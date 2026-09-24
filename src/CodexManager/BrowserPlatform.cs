namespace CodexManager;

// Assigned by the browser entry point; native builds never use browser storage.
public static class BrowserPlatform
{
    public static string Origin { get; set; } = "";
    public static Action<string> Navigate { get; set; } = _ => { };
    public static Func<string, string?> Read { get; set; } = _ => null;
    public static Action<string, string> Write { get; set; } = (_, _) => throw new InvalidOperationException("Browser storage is unavailable.");
    public static Action<string, byte[]> Download { get; set; } = (_, _) => { };
    public static Func<string, int, Task> DownloadUrl { get; set; } = (_, _) => Task.CompletedTask;
}
