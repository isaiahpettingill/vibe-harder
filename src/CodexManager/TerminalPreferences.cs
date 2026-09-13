#if !MOBILE_CLIENT
using Porta.Pty;
#endif

namespace CodexManager;

public sealed record ShellChoice(string Id, string Name, string Executable, string[] Arguments)
{
    public override string ToString() => Name;
}

public static class TerminalPreferences
{
    public static string Platform(Workspace workspace) => workspace.IsWsl ? "wsl:" + workspace.Distro : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    public static IReadOnlyList<ShellChoice> WindowsShells()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? Find(string file, params string[] paths) => paths.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), file))).FirstOrDefault(File.Exists);
        var choices = new List<ShellChoice>();
        void Add(string id, string name, string file, string[] args, params string[] paths)
        { if (Find(file, paths) is { } executable) choices.Add(new(id, name, executable, args)); }
        Add("pwsh", "PowerShell 7 (pwsh)", "pwsh.exe", ["-NoLogo"], Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        Add("git-bash", "Git Bash", "__git_bash__", ["--login", "-i"], Path.Combine(programFiles, "Git", "bin", "bash.exe"), Path.Combine(local, "Programs", "Git", "bin", "bash.exe"), Path.Combine(home, "scoop", "apps", "git", "current", "bin", "bash.exe"));
        Add("msys2", "MSYS2", "__msys2__", ["--login", "-i"], @"C:\msys64\usr\bin\bash.exe", @"C:\msys32\usr\bin\bash.exe");
        Add("cygwin", "Cygwin", "__cygwin__", ["--login", "-i"], @"C:\cygwin64\bin\bash.exe", @"C:\cygwin\bin\bash.exe");
        Add("powershell", "Windows PowerShell", "powershell.exe", ["-NoLogo"], Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        Add("cmd", "Command Prompt (cmd.exe)", "cmd.exe", ["/d"], Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        Add("nu", "Nushell", "nu.exe", [], Path.Combine(programFiles, "nu", "bin", "nu.exe"), Path.Combine(local, "Programs", "nu", "bin", "nu.exe"));
        return choices;
    }
#if !MOBILE_CLIENT
    public static void Apply(PtyOptions options, Workspace workspace, Store store)
    {
        var platform = Platform(workspace);
        var command = store.Setting("terminalCommand:" + platform)?.Trim();
        if (platform == "windows")
        {
            var selected = store.Setting("terminalShell:windows");
            if (selected == "custom")
            {
                if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Set a custom terminal command in Terminal settings.");
                options.App = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                options.VerbatimCommandLine = true;
                options.CommandLine = ["/d", "/s", "/c", "\"" + command + "\""];
                return;
            }
            var installed = WindowsShells();
            var shell = string.IsNullOrEmpty(selected) ? installed.FirstOrDefault() : installed.FirstOrDefault(s => s.Id == selected);
            if (shell is null) throw new InvalidOperationException("The selected shell is no longer installed. Choose another in Terminal settings.");
            options.App = shell.Executable; options.CommandLine = shell.Arguments;
            if (shell.Id is "git-bash" or "msys2" or "cygwin") options.Environment["CHERE_INVOKING"] = "1";
            if (shell.Id == "msys2") options.Environment["MSYSTEM"] = "MSYS";
        }
        else if (!string.IsNullOrWhiteSpace(command))
        {
            if (workspace.IsWsl)
                options.CommandLine = [.. options.CommandLine, "--exec", "sh", "-lc", Hosts.WindowsArgument(command)];
            else { options.App = "/bin/sh"; options.CommandLine = ["-lc", Hosts.LocalShellCommand(command)]; }
        }
    }
#endif
}
