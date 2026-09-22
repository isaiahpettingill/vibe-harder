using System.Diagnostics;
using System.Text;

namespace CodexManager;

public static class Hosts
{
    public const string DefaultAdapter = "npx -y @agentclientprotocol/codex-acp@latest";
    public static string ResourceDirectory => OperatingSystem.IsMacOS() && Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) == "MacOS" && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Resources")) ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources")) : AppContext.BaseDirectory;
    public static string WindowsShellCommand(string command)
    {
        if (command.StartsWith("npx ", StringComparison.Ordinal)) return "npx.cmd " + command[4..];
        foreach (var name in new[] { "npm", "opencode", "codex", "claude", "dirac", "pi", "pi-acp", "cline", "vtcode" })
            if (command == name || command.StartsWith(name + " ", StringComparison.Ordinal))
                return "& $(if (Get-Command " + name + ".cmd -ErrorAction SilentlyContinue) { '" + name + ".cmd' } else { '" + name + "' })" + command[name.Length..];
        return command;
    }
    public static string LocalShellCommand(string command)
    {
        if (OperatingSystem.IsWindows()) return command;
        var paths = new List<string>();
        // Finder does not inherit an interactive shell's PATH.
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        paths.Add(Path.Combine(user, ".local", "bin"));
        paths.Add(Path.Combine(user, ".opencode", "bin"));
        paths.Add(Path.Combine(user, ".bun", "bin"));
        if (OperatingSystem.IsMacOS()) { paths.Add("/opt/homebrew/bin"); paths.Add("/usr/local/bin"); }
        return "export PATH=" + Quote(string.Join(':', paths)) + ":\"$PATH\"; " + command;
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static string WslShellCommand(string command) =>
        // Login bash does not load zsh/nvm/fnm PATH setup. Read only PATH from
        // the user's interactive shell; startup banners must never reach ACP stdout.
        "vibe_agent_path=$(timeout 10s \"${SHELL:-/bin/bash}\" -ilc 'command printenv PATH >&3' 3>&1 >/dev/null 2>/dev/null </dev/null); " +
        "if [ -n \"$vibe_agent_path\" ]; then export PATH=\"$vibe_agent_path\"; fi; " +
        "export PATH=\"$HOME/.opencode/bin:$HOME/.local/bin:$HOME/.bun/bin:$PATH\"; exec " + command;
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
    public static ProcessStartInfo Agent(Workspace workspace, string command, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo info;
        if (workspace.IsWsl)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WSL workspaces require Windows.");
            if (environment is not null) command = "env " + string.Join(" ", environment.Select(pair => Quote(pair.Key + "=" + pair.Value))) + " " + command;
            info = Info("wsl.exe", "--distribution", workspace.Distro!, "--cd", workspace.Path, "--exec", "bash", "-lc", WslShellCommand(command));
        }
        else if (OperatingSystem.IsWindows())
        {
            info = Info("powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", WindowsShellCommand(command));
            info.WorkingDirectory = workspace.Path;
            var paths = new[] { Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims") };
            paths = [.. paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VT Code")];
            info.Environment["PATH"] = string.Join(Path.PathSeparator, paths.Where(p => !string.IsNullOrWhiteSpace(p)).SelectMany(p => Environment.ExpandEnvironmentVariables(p!).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            info = Info("/bin/sh", "-lc", LocalShellCommand("exec " + command)); info.WorkingDirectory = workspace.Path;
        }
        if (!workspace.IsWsl && environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
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
    public static async Task<string> CreateDirectory(string? distro, string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Enter a folder name without path separators.");
        if (distro is not null)
        {
            await Validate(new Workspace("new-folder", "", parent, distro));
            var destination = parent.TrimEnd('/') + "/" + name;
            await Capture(Info("wsl.exe", "-d", distro, "--exec", "mkdir", "--", destination));
            return destination;
        }
        return await Task.Run(() =>
        {
            if (!Path.IsPathFullyQualified(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing parent folder first.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ')))
                throw new ArgumentException("This folder name contains invalid characters.");
            var destination = Path.Combine(parent, name);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("A folder or file with this name already exists.");
            return Directory.CreateDirectory(destination).FullName;
        });
    }
}
