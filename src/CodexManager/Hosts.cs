using System.Diagnostics;
using System.Text;

namespace CodexManager;

public static class Hosts
{
    public const string DefaultAdapter = "npx -y @agentclientprotocol/codex-acp@1.11.0";
    public static string BundledNodeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime", "node", OperatingSystem.IsWindows() ? "" : "bin");
    public static string WindowsShellCommand(string command) => command.StartsWith("npx ", StringComparison.Ordinal) ? "npx.cmd " + command[4..] : command;
    public static string LocalShellCommand(string command)
    {
        if (OperatingSystem.IsWindows()) return command;
        var paths = new List<string>();
        if (Directory.Exists(BundledNodeDirectory)) paths.Add(BundledNodeDirectory);
        // Finder does not inherit an interactive shell's PATH.
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        paths.Add(Path.Combine(user, ".local", "bin"));
        paths.Add(Path.Combine(user, ".opencode", "bin"));
        if (OperatingSystem.IsMacOS()) { paths.Add("/opt/homebrew/bin"); paths.Add("/usr/local/bin"); }
        return "export PATH=" + Quote(string.Join(':', paths)) + ":\"$PATH\"; " + command;
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static string WindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }
    public static ProcessStartInfo Agent(Workspace workspace, string command)
    {
        ProcessStartInfo info;
        if (workspace.IsWsl)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WSL workspaces require Windows.");
            info = Info("wsl.exe", "--distribution", workspace.Distro!, "--cd", workspace.Path, "--exec", "bash", "-lc", "exec " + command);
        }
        else if (OperatingSystem.IsWindows())
        {
            info = Info("powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", WindowsShellCommand(command));
            info.WorkingDirectory = workspace.Path;
        }
        else
        {
            info = Info("/bin/sh", "-lc", LocalShellCommand("exec " + command)); info.WorkingDirectory = workspace.Path;
        }
        return info;
    }
    public static ProcessStartInfo Info(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static async Task<string> Capture(ProcessStartInfo info, TimeSpan? duration = null)
    {
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + info.FileName);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(duration ?? TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException(error.Trim());
        return output;
    }
    public static async Task<string[]> Distros()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var info = Info("wsl.exe", "--list", "--quiet"); info.StandardOutputEncoding = Encoding.Unicode;
        return (await Capture(info)).Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    public static async Task Validate(Workspace workspace)
    {
        if (workspace.IsWsl)
        {
            if (!workspace.Path.StartsWith('/')) throw new ArgumentException("Enter an absolute Linux path, such as /home/you/project.");
            await Capture(Info("wsl.exe", "-d", workspace.Distro!, "--exec", "test", "-d", workspace.Path));
        }
        else if (!System.IO.Path.IsPathFullyQualified(workspace.Path) || !Directory.Exists(workspace.Path)) throw new DirectoryNotFoundException("Choose an existing absolute folder path.");
    }
    public static async Task<string[]> Directories(string? distro, string path)
    {
        if (distro is null) return await Task.Run(() => Directory.GetDirectories(path).Order().ToArray());
        var output = await Capture(Info("wsl.exe", "-d", distro, "--exec", "find", "-L", path, "-mindepth", "1", "-maxdepth", "1", "-type", "d", "-print0"));
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order().ToArray();
    }
}
