namespace CodexManager;

// Installed by the Android activity; desktop builds have no app-lock settings.
public interface IMobileAppSecurity
{
    bool Enabled { get; }
    Task<string> ChangeEnabled(bool enabled);
    // How long the app may be away before returning to it needs authentication again.
    TimeSpan LockAfter { get; set; }
}

public static class MobileAppSecurity
{
    public static IMobileAppSecurity? Current { get; set; }
    public static readonly (TimeSpan Delay, string Label)[] LockDelays =
        [(TimeSpan.Zero, "Immediately"), (TimeSpan.FromSeconds(30), "After 30 seconds"), (TimeSpan.FromMinutes(1), "After 1 minute"), (TimeSpan.FromMinutes(5), "After 5 minutes"), (TimeSpan.FromMinutes(15), "After 15 minutes")];
    private static object? filePickerPause;

    // Exempt only the next pause caused by our own picker. Consume it immediately
    // so a later Home/app-switch pause cannot inherit the exemption.
    public static IDisposable BeginFilePicker()
    {
        var ticket = new object();
        Interlocked.Exchange(ref filePickerPause, ticket);
        return new PickerScope(ticket);
    }
    public static bool ConsumeFilePickerPause() => Interlocked.Exchange(ref filePickerPause, null) is not null;
    private sealed class PickerScope(object ticket) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref filePickerPause, null, ticket);
    }
}
