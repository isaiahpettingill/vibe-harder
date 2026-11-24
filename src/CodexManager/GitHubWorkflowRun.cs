using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed partial record GitHubWorkflowRun
{
    private GitHubWorkflowRun(string repository, ulong id) => (Repository, Id) = (repository, id);

    public string Repository { get; }
    public ulong Id { get; }
    public string Url => $"https://github.com/{Repository}/actions/runs/{Id}";

    [GeneratedRegex(@"\A(?i:https://github\.com)/(?<owner>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<repo>[A-Za-z0-9_.-]{1,100})/actions/runs/(?<id>[0-9]{1,20})/?(?:[?#][^\s]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunUrl();

    [GeneratedRegex("https?://[^\\s<>\\[\\]()\"'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Links();

    public static GitHubWorkflowRun? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = RunUrl().Match(text.Trim());
        if (!match.Success || match.Groups["repo"].Value is "." or ".." ||
            !ulong.TryParse(match.Groups["id"].ValueSpan, out var id) || id == 0) return null;
        return new((match.Groups["owner"].Value + "/" + match.Groups["repo"].Value).ToLowerInvariant(), id);
    }

    public static GitHubWorkflowRun? FindInMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        GitHubWorkflowRun? found = null;
        foreach (Match match in Links().Matches(text))
        {
            if (Parse(match.Value.TrimEnd('.', ',', ';', '!', ':')) is not { } candidate) continue;
            if (found is not null && found != candidate) return null;
            found = candidate;
        }
        return found;
    }

    public static async Task<GitHubWorkflowRun> FromOrigin(Workspace workspace, string id)
    {
        const string usage = "Pass OWNER/REPO and a run ID, or paste the full GitHub Actions URL.";
        if (id.Length is < 1 or > 20 || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id, out var number) || number == 0)
            throw new IOException("Enter a positive GitHub Actions run ID.");
        string origin;
        try { origin = (await Hosts.Capture(Hosts.Agent(workspace, "git remote get-url origin"), TimeSpan.FromSeconds(5))).Trim(); }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { throw new IOException("Could not find a GitHub origin. " + usage, error); }
        const string ssh = "git@github.com:";
        const string sshUrl = "ssh://git@github.com/";
        if (origin.StartsWith(ssh, StringComparison.OrdinalIgnoreCase)) origin = "https://github.com/" + origin[ssh.Length..];
        if (origin.StartsWith(sshUrl, StringComparison.OrdinalIgnoreCase)) origin = "https://github.com/" + origin[sshUrl.Length..];
        origin = origin.TrimEnd('/');
        if (origin.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) origin = origin[..^4];
        if (origin.IndexOfAny(['#', '?']) >= 0) throw new IOException("The workspace origin is not a GitHub repository. " + usage);
        return Parse($"{origin}/actions/runs/{number}") ?? throw new IOException("The workspace origin is not a GitHub repository. " + usage);
    }

    public async Task<bool> IsComplete(Workspace workspace)
    {
        // Only validated repository characters and the numeric run ID reach the shell.
        var process = Hosts.Agent(workspace, $"gh run view {Id} --repo github.com/{Repository} --json status",
            new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1" });
        return IsComplete(await Hosts.Capture(process, TimeSpan.FromSeconds(15)));
    }

    internal static bool IsComplete(string json)
    {
        using var result = JsonDocument.Parse(json);
        return result.RootElement.ValueKind == JsonValueKind.Object && result.RootElement.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String && status.ValueEquals("completed");
    }
}
