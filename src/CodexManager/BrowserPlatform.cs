namespace CodexManager;

// Assigned by the browser entry point; native builds never use browser storage.
public static class BrowserPlatform
{
    public static string Origin { get; set; } = "";
    public static Func<string, string?> Read { get; set; } = _ => null;
    public static Action<string, string> Write { get; set; } = (_, _) => throw new InvalidOperationException("Browser storage is unavailable.");
    public static Action<string, byte[]> Download { get; set; } = (_, _) => { };
}
