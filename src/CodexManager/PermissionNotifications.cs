using System.Diagnostics;
using System.Security;
using System.Text;
using Avalonia.Threading;

namespace CodexManager;

public static class PermissionNotifications
{
    public static Action<string, string, Action>? Mobile { get; set; }
    public static Action<string>? MobileDismiss { get; set; }
    public static void Dismiss(string id) => MobileDismiss?.Invoke(id);
    public static void Show(string id, string chat, Action activate)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) { Mobile?.Invoke(id, chat, activate); return; }
        _ = Desktop(chat, activate);
    }
    private static async Task Desktop(string chat, Action activate)
    {
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows())
            {
                const string appId = "VibeHarder";
                using var registration = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\Classes\\AppUserModelId\\" + appId);
                registration.SetValue("DisplayName", "Vibe Harder");
                var xml = "<toast><visual><binding template=\"ToastGeneric\"><text>Permission needed</text><text>" + SecurityElement.Escape(chat) + "</text></binding></visual></toast>";
                var script = "$ErrorActionPreference='Stop'; [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] > $null; [Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime] > $null; $xml=New-Object Windows.Data.Xml.Dom.XmlDocument; $xml.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)) + "'))); $toast=[Windows.UI.Notifications.ToastNotification]::new($xml); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + appId + "').Show($toast)";
                start.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            }
            else if (OperatingSystem.IsMacOS())
            {
                start.FileName = "/usr/bin/osascript"; start.ArgumentList.Add("-e");
                start.ArgumentList.Add("on run argv\ndisplay notification (item 1 of argv) with title \"Vibe Harder: permission needed\"\nend run"); start.ArgumentList.Add(chat);
            }
            else
            {
                start.FileName = "notify-send";
                foreach (var arg in new[] { "--app-name=Vibe Harder", "--icon=codex-manager", "--urgency=normal", "--action=open=Open chat", "--wait", "--expire-time=30000", "Permission needed", chat }) start.ArgumentList.Add(arg);
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
