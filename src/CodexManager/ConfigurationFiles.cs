using System.Diagnostics;

namespace CodexManager;

public sealed record ConfigurationFile(string Title, string Path, string? Distro = null)
{
    public string EditorPath => Distro is null ? Path : $@"\\wsl.localhost\{Distro}{Path.Replace('/', '\\')}";
}

public static class ConfigurationFiles
{
    private static readonly string[] Variables = ["HOME", "USERPROFILE", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR", "OPENCODE_CONFIG", "OPENCODE_DISABLE_CLAUDE_CODE", "OPENCODE_DISABLE_CLAUDE_CODE_PROMPT"];

    public static async Task<List<ConfigurationFile>> Find(Workspace? workspace)
    {
        var environment = new Dictionary<string, string?>();
        if (workspace?.IsWsl == true)
        {
            // Query only path-related variables from the same login shell used by the adapter.
            var script = "printf '%s\\0' " + string.Join(" ", Variables.Select(v => "\"${" + v + "-}\""));
            var values = (await Hosts.Capture(Hosts.Info("wsl.exe", "-d", workspace.Distro!, "--cd", workspace.Path, "--exec", "bash", "-lc", script))).Split('\0');
            for (var i = 0; i < Variables.Length; i++) environment[Variables[i]] = values.ElementAtOrDefault(i);
        }
        else foreach (var variable in Variables) environment[variable] = Environment.GetEnvironmentVariable(variable);
        var candidates = Candidates(environment, workspace?.Distro, workspace?.Path);
        return await Task.Run(() => candidates.Where(f => File.Exists(f.EditorPath)).ToList());
    }

    public static List<ConfigurationFile> Candidates(IReadOnlyDictionary<string, string?> environment, string? distro = null, string? cwd = null)
    {
        var windows = distro is null && OperatingSystem.IsWindows();
        string? Env(string name) => environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        var home = (windows ? Env("USERPROFILE") : Env("HOME")) ?? Env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Join(string root, string name) => root.TrimEnd('/', '\\') + (windows ? "\\" : "/") + name;
        string Expand(string value)
        {
            if (value == "~") return home;
            if (value.StartsWith("~/") || value.StartsWith("~\\")) value = Join(home, value[2..]);
            if (windows) return System.IO.Path.GetFullPath(value, cwd ?? home);
            if (!value.StartsWith('/')) value = Join(cwd ?? home, value);
            var parts = new List<string>();
            foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); } else if (part != ".") parts.Add(part);
            return "/" + string.Join('/', parts);
        }
        var codex = Expand(Env("CODEX_HOME") ?? Join(home, ".codex"));
        var claude = Expand(Env("CLAUDE_CONFIG_DIR") ?? Join(home, ".claude"));
        var xdg = Env("XDG_CONFIG_HOME");
        if (xdg is null || !(windows ? System.IO.Path.IsPathFullyQualified(xdg) : xdg.StartsWith('/'))) xdg = Join(home, ".config");
        var opencode = Expand(Join(xdg, "opencode"));
        List<ConfigurationFile> files = [];
        void Add(string title, string path) => files.Add(new(title, Expand(path), distro));
        Add("Codex: global AGENTS.override.md", Join(codex, "AGENTS.override.md"));
        Add("Codex: global AGENTS.md", Join(codex, "AGENTS.md"));
        Add("Codex: config.toml", Join(codex, "config.toml"));
        Add("Claude: global CLAUDE.md", Join(claude, "CLAUDE.md"));
        Add("Claude: settings.json", Join(claude, "settings.json"));
        Add("Claude: global .claude.json", Env("CLAUDE_CONFIG_DIR") is null ? Join(home, ".claude.json") : Join(claude, ".claude.json"));
        void OpenCodeDirectory(string root, string label)
        {
            foreach (var name in new[] { "AGENTS.md", "config.json", "opencode.json", "opencode.jsonc", "tui.json", "tui.jsonc" }) Add($"OpenCode: {label} {name}", Join(root, name));
        }
        OpenCodeDirectory(opencode, "global");
        if (Env("OPENCODE_CONFIG_DIR") is { } custom) OpenCodeDirectory(Expand(custom), "custom");
        if (Env("OPENCODE_CONFIG") is { } config) Add("OpenCode: OPENCODE_CONFIG", config);
        bool Disabled(string name) => Env(name) is "1" || string.Equals(Env(name), "true", StringComparison.OrdinalIgnoreCase);
        if (!Disabled("OPENCODE_DISABLE_CLAUDE_CODE") && !Disabled("OPENCODE_DISABLE_CLAUDE_CODE_PROMPT"))
            Add("OpenCode: Claude instructions fallback", Join(Join(home, ".claude"), "CLAUDE.md"));
        return files.DistinctBy(f => (f.Title, f.Path)).ToList();
    }

    public static ProcessStartInfo EditorStartInfo(ConfigurationFile file) => new(file.EditorPath) { UseShellExecute = true, Verb = "open" };
    public static void Open(ConfigurationFile file)
    {
        if (!File.Exists(file.EditorPath)) throw new FileNotFoundException("This configuration file no longer exists.", file.EditorPath);
        Process.Start(EditorStartInfo(file));
    }
}
