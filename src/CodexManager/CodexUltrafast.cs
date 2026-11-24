using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace CodexManager;

internal static class CodexUltrafast
{
    internal const string SettingKey = "codexUltrafast";
    internal const string Argument = "--vibe-harder-codex-ultrafast";
    private const string FullAccessKey = "VIBE_HARDER_ULTRAFAST_FULL_ACCESS";
    private const string Script = "codex-ultrafast.cjs";
    private static readonly Lazy<string> Bridge = new(() =>
    {
        using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://VibeHarder.UI/Assets/Agents/" + Script)));
        return reader.ReadToEnd();
    });

    internal static bool IsManagedAdapter(string command) =>
        (command.StartsWith("npx -y --package=@agentclientprotocol/codex-acp@", StringComparison.Ordinal) ||
         command.StartsWith("npx -y --prefer-offline --package=@agentclientprotocol/codex-acp@", StringComparison.Ordinal)) &&
        command.Contains(" --package=@openai/codex@", StringComparison.Ordinal) && command.Contains(" -- codex-acp", StringComparison.Ordinal);

    internal static bool Requested(string command) => Regex.IsMatch(command, $@"(?<!\S){Argument}(?!\S)");
    internal static string RemoveArgument(string command) => Regex.Replace(command, $@"(?<!\S){Argument}(?!\S)", "").Trim();

    internal static ProcessStartInfo Start(Workspace workspace, string command, Dictionary<string, string> environment, bool fullAccess, string? profile)
    {
        // Apply only the approval settings that the normal Codex launcher resolved.
        environment[FullAccessKey] = fullAccess ? "1" : "0";
        if (workspace.IsWsl)
        {
            environment.Remove("CODEX_PATH");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Bridge.Value));
            var setup = $$"""
                vibe_codex_dir="$HOME/.local/share/vibe-harder/codex-ultrafast"
                (umask 077; mkdir -p "$vibe_codex_dir") || exit 1
                vibe_codex_tmp=$(mktemp -d "$vibe_codex_dir/install.XXXXXX") || exit 1
                if ! (
                  printf '%s' '{{encoded}}' | base64 -d > "$vibe_codex_tmp/{{Script}}" &&
                  printf '%s\n' '#!/bin/sh' 'exec node "$(dirname "$0")/{{Script}}" "$@"' > "$vibe_codex_tmp/codex" &&
                  chmod 700 "$vibe_codex_tmp/codex" &&
                  mv -f "$vibe_codex_tmp/{{Script}}" "$vibe_codex_dir/{{Script}}" &&
                  mv -f "$vibe_codex_tmp/codex" "$vibe_codex_dir/codex"
                ); then rm -f -- "$vibe_codex_tmp/{{Script}}" "$vibe_codex_tmp/codex"; rmdir "$vibe_codex_tmp"; exit 1; fi
                rmdir "$vibe_codex_tmp"
                export CODEX_PATH="$vibe_codex_dir/codex"
                """.ReplaceLineEndings("\n");
            return Hosts.Agent(workspace, command, environment, setup);
        }

        var directory = Path.Combine(profile ?? Store.DataDirectory, "codex-ultrafast");
        Directory.CreateDirectory(directory);
        Install(Path.Combine(directory, Script), Bridge.Value);
        var wrapper = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        Install(wrapper, OperatingSystem.IsWindows()
            ? $"@echo off\r\nnode \"%~dp0{Script}\" %*\r\n"
            : $"#!/bin/sh\nexec node \"$(dirname \"$0\")/{Script}\" \"$@\"\n", executable: true);
        environment["CODEX_PATH"] = wrapper;
        return Hosts.Agent(workspace, command, environment);
    }

    private static void Install(string path, string content, bool executable = false)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content);
            if (executable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
