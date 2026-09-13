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
}
