namespace CodexManager;

// vibeharder://chat/<id> opens a chat. Windows toasts launch it when clicked: an unpackaged app
// has no activation handler, so the link starts VibeHarder, which forwards it to the running app.
public static class ChatLinks
{
    public const string Scheme = "vibeharder";
    public static string For(string chatId) => Scheme + "://chat/" + Uri.EscapeDataString(chatId);
    public static bool TryParse(string? link, out string chatId)
    {
        chatId = "";
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Scheme || uri.Host != "chat") return false;
        chatId = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        return chatId.Length is > 0 and <= 128 && chatId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    }

    private static bool registered;
    // Per-user registration, refreshed so it follows the app when it moves or updates.
    public static void RegisterWindows()
    {
        if (registered || !OperatingSystem.IsWindows() || Environment.ProcessPath is not { } executable) return;
        registered = true;
        try
        {
            using (var app = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\VibeHarder"))
            {
                app.SetValue("DisplayName", "Vibe Harder");
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
                if (File.Exists(icon)) app.SetValue("IconUri", icon);
            }
            using var scheme = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            scheme.SetValue("", "URL:Vibe Harder"); scheme.SetValue("URL Protocol", "");
            using (var icon = scheme.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + executable + "\",0");
            using var command = scheme.CreateSubKey(@"shell\open\command");
            command.SetValue("", "\"" + executable + "\" \"%1\"");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { AppDiagnostics.Record("Register notifications", error); }
    }
}
