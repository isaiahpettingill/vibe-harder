using System.Security;
using Microsoft.Win32;

namespace CodexManager;

public static class StartupRegistration
{
    public static string WindowsCommand(string executable) => "\"" + executable + "\" --startup";
    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the application executable.");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("CodexManager", WindowsCommand(executable)); else key.DeleteValue("CodexManager", false);
            return;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "LaunchAgents", "com.codexmanager.app.plist")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "autostart", "codex-manager.desktop");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = OperatingSystem.IsMacOS()
            ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>com.codexmanager.app</string><key>ProgramArguments</key><array><string>" + SecurityElement.Escape(executable) + "</string><string>--startup</string></array><key>RunAtLoad</key><true/></dict></plist>"
            : "[Desktop Entry]\nType=Application\nName=Vibe Harder\nExec=\"" + executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\" --startup\nTerminal=false\n";
        File.WriteAllText(path, content);
    }
}
