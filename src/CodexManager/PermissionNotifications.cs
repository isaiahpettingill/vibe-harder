using System.Diagnostics;
using System.Security;
using System.Text;
using Avalonia.Threading;

namespace CodexManager;

public static class PermissionNotifications
{
    public static Action<string, string, Action>? Mobile { get; set; }
    public static Action<string>? MobileDismiss { get; set; }
    // Replaces the system notifier (tests record notifications instead of showing them).
    public static Action<string, string, Action, string>? DesktopOverride { get; set; }
    public static string? LastLink { get; private set; }
    public static void Dismiss(string id) => MobileDismiss?.Invoke(id);
    // link (vibeharder://chat/<id>) reopens the chat from a Windows toast, which has no callback.
    public static void Show(string id, string chat, Action activate, string title = "Permission needed", string? link = null)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) { Mobile?.Invoke(id, chat, activate); return; }
        if (DesktopOverride is { } replace) { LastLink = link; replace(id, chat, activate, title); return; }
        _ = Desktop(chat, activate, title, link);
    }
    private static async Task Desktop(string chat, Action activate, string title, string? link)
    {
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows())
            {
                const string appId = "VibeHarder";
                // Registers the app's display name, icon, and the link a click opens.
                ChatLinks.RegisterWindows();
                var launch = link is null ? "" : " launch=\"" + SecurityElement.Escape(link) + "\" activationType=\"protocol\"";
                // Windows caches an unpackaged app's header icon; the logo in the toast always shows.
                var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
                var image = File.Exists(logo) ? "<image placement=\"appLogoOverride\" src=\"" + SecurityElement.Escape(new Uri(logo).AbsoluteUri) + "\"/>" : "";
                var xml = "<toast" + launch + "><visual><binding template=\"ToastGeneric\"><text>" + SecurityElement.Escape(title) + "</text><text>" + SecurityElement.Escape(chat) + "</text>" + image + "</binding></visual></toast>";
                var script = "$ErrorActionPreference='Stop'; [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] > $null; [Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime] > $null; $xml=New-Object Windows.Data.Xml.Dom.XmlDocument; $xml.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)) + "'))); $toast=[Windows.UI.Notifications.ToastNotification]::new($xml); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + appId + "').Show($toast)";
                start.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            }
            else if (OperatingSystem.IsMacOS())
            {
                start.FileName = "/usr/bin/osascript"; start.ArgumentList.Add("-e");
                start.ArgumentList.Add("on run argv\ndisplay notification (item 1 of argv) with title (item 2 of argv)\nend run"); start.ArgumentList.Add(chat); start.ArgumentList.Add("Vibe Harder: " + title);
            }
            else
            {
                start.FileName = "notify-send";
                foreach (var arg in new[] { "--app-name=Vibe Harder", "--icon=codex-manager", "--urgency=normal", "--action=open=Open chat", "--wait", "--expire-time=30000", title, chat }) start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start);
            if (process is null) return;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } return; }
            if ((await output).Trim() == "open") Dispatcher.UIThread.Post(activate);
            if (process.ExitCode != 0) AppDiagnostics.Record("Permission notification", new IOException(await errors));
        }
        catch (Exception error) { AppDiagnostics.Record("Permission notification", error); }
    }
}
