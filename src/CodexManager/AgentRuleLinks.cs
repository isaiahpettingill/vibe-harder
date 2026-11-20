using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexManager;

internal static partial class AgentRuleLinks
{
    internal const string Prefix = "agent-wrangler:";
    internal const string SettingKey = "agentWranglerRuleLinks";
    internal static bool Desktop => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS() && !OperatingSystem.IsBrowser();
    internal static readonly Lazy<string?> Installed = new(FindExecutable);

    [GeneratedRegex(@"(?<![\w-])AW-[0-9]{1,19}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex Citations();

    [GeneratedRegex(@"^(?:<<<<<<< Your edit|\|\|\|\|\|\|\| Original rule|=======|>>>>>>> Agent Wrangler)\r?$", RegexOptions.Multiline)]
    private static partial Regex ConflictMarkers();

    internal static string? Parse(string target)
    {
        if (!target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = target.AsSpan(Prefix.Length);
        if (!id.StartsWith("AW-", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(id[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1) return null;
        return "AW-" + number.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string? FindExecutable()
    {
        if (!Desktop) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] paths = [Environment.GetEnvironmentVariable("PATH"), Path.Combine(home, ".local", "bin"), Path.Combine(home, "go", "bin")];
        if (OperatingSystem.IsWindows())
            paths = [.. paths, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)];
        var name = OperatingSystem.IsWindows() ? "agent-wrangler.exe" : "agent-wrangler";
        foreach (var path in paths.OfType<string>().SelectMany(value => Environment.ExpandEnvironmentVariables(value).Split(Path.PathSeparator)))
        {
            var folder = path.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static async Task<string> Run(string id, string? body = null, string? previous = null, CancellationToken token = default)
    {
        if (Parse(Prefix + id) != id) throw new ArgumentException("Invalid Agent Wrangler rule ID.", nameof(id));
        var executable = Installed.Value ?? FindExecutable() ?? throw new IOException("Install Agent Wrangler and add its executable to your PATH.");
        var info = body is null ? Hosts.Info(executable, "rule", "show", id, "--raw")
            : Hosts.Info(executable, "rule", "edit", id, "--file", "-", "--raw", "--if-text-sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous!))));
        var result = await Capture(info, body, token);
        if (result.Code == 0) return result.Output;
        var error = result.Error.Trim();
        if (error.StartsWith($"rule {id} changed since it was opened", StringComparison.Ordinal)) throw new RuleChangedException();
        throw new IOException(error.Length == 0 ? "Agent Wrangler could not access the rule." : error);
    }

    private sealed class RuleChangedException : IOException { }

    internal static async Task<(string Text, bool HasConflicts)> Merge(string mine, string original, string current, CancellationToken token)
    {
        if (original == current) return (mine, false);
        var folder = Directory.CreateTempSubdirectory("vibe-harder-merge-");
        try
        {
            var paths = new[] { "edit", "original", "current" }.Select(name => Path.Combine(folder.FullName, name)).ToArray();
            var contents = new[] { mine, original, current };
            for (var i = 0; i < paths.Length; i++) await File.WriteAllTextAsync(paths[i], contents[i], token);
            var info = Hosts.Info("git", "merge-file", "--diff3", "--stdout", "-L", "Your edit", "-L", "Original rule", "-L", "Agent Wrangler", "--", paths[0], paths[1], paths[2]);
            var result = await Capture(info, null, token);
            if (result.Code is < 0 or > 127) throw new IOException("Could not merge the Agent Wrangler rule: " + result.Error.Trim());
            return (result.Output, result.Code != 0);
        }
        finally { folder.Delete(true); }
    }

    internal static async Task Watch(string path, string original, Func<CancellationToken, Task<string>> load,
        Func<string, string, CancellationToken, Task<string>> save, Action<bool, string?> report, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        string? handled = original;
        var observed = original;
        var conflictPending = false;
        while (await timer.WaitForNextTickAsync(token))
        {
            string text;
            try { text = await File.ReadAllTextAsync(path, token); }
            catch (IOException) { continue; } // An editor can briefly remove or lock the file during an atomic save.
            if (text != observed) { observed = text; continue; }
            if (text == handled) continue;
            handled = text;
            if (conflictPending && ConflictMarkers().IsMatch(text))
            { report(true, null); continue; }
            try
            {
                var current = await load(token);
                var (merged, conflicts) = await Merge(text, original, current, token);
                if (await File.ReadAllTextAsync(path, token) != text) { handled = null; continue; }
                if (merged != text)
                {
                    await File.WriteAllTextAsync(path, merged, token);
                    observed = handled = merged;
                }
                original = current;
                conflictPending = conflicts;
                if (conflicts) { report(true, null); continue; }
                original = await save(merged, current, token);
                report(false, null);
            }
            catch (RuleChangedException) { handled = null; } // Re-read and merge a change made during this save.
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                report(false, error.Message);
            }
        }
    }

    private static async Task<(int Code, string Output, string Error)> Capture(ProcessStartInfo info, string? input, CancellationToken token)
    {
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + info.FileName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            try { if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token); }
            catch (IOException) { } // Read the CLI's error if it rejected the command before reading input.
            finally { process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
}
