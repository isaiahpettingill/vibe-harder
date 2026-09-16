namespace CodexManager;

// Installed by the Android activity; desktop builds have no app-lock settings.
public interface IMobileAppSecurity
{
    bool Enabled { get; }
    Task<string> ChangeEnabled(bool enabled);
}

public static class MobileAppSecurity
{
    public static IMobileAppSecurity? Current { get; set; }
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
