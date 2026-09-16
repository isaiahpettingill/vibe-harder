using System.Text.RegularExpressions;

namespace CodexManager;

public static class VtCodeLaunch
{
    public const string AuthenticationOption = "vibe_openai_authentication";
    public static string? FailureHelp(string error) => error.Contains("Stream must be set to true", StringComparison.OrdinalIgnoreCase)
        ? "VT Code’s ACP adapter sent a non-streaming request to a streaming-only endpoint. This is an adapter compatibility error, not a login failure. Update VT Code on the computer running this chat and reconnect. If it persists, the adapter needs a streaming fix. Your selected authentication method has not been changed. [VT Code releases](https://github.com/vinhnx/VTCode/releases)"
        : null;
    public static string AuthenticationKey(Workspace workspace) => "VTCode:" + (workspace.IsWsl ? "wsl" : "local") + "OpenAiAuthentication";
    public static string Method(Store store, Workspace workspace) => store.Setting(AuthenticationKey(workspace)) == "api_key" ? "api_key" : "chatgpt";
    public static SessionConfig AuthenticationBadge(string method, bool verified) => new(AuthenticationOption,
        "OpenAI authentication — change in Settings > Agents > VT Code, then reconnect",
        "select", method, [new(method, !verified ? "OpenAI auth unverified" : method == "api_key" ? "API key · billed separately" : "ChatGPT subscription")]);
    public static string WithAuthentication(string command, IReadOnlySet<string>? authenticated, string method = "chatgpt") => Prepare(command, authenticated, method).Command;
    public static void EnsureAuthentication(IReadOnlyList<SessionConfig> options, bool openAiPinned)
    {
        if (!openAiPinned && options.FirstOrDefault(c => c.Id == "provider")?.Current is null or "openai")
            throw new IOException("OpenAI authentication is unverified. Set the VT Code command to vtcode --provider openai acp in Settings > Agents, then reconnect before sending.");
    }
    public static (string Command, bool OpenAiPinned) Prepare(string command, IReadOnlySet<string>? authenticated, string method)
    {
        if (method is not "chatgpt" and not "api_key") throw new ArgumentException("Choose ChatGPT subscription or API key.");
        var tokens = Regex.Matches(command, "\"[^\"]*\"|'[^']*'|[^\\s]+")
            .Select(m => (Text: m.Value.Trim('\"', '\''), End: m.Index + m.Length)).ToArray();
        var executable = tokens.FirstOrDefault().Text?.Replace('\\', '/').Split('/')[^1];
        if (!new[] { "vtcode", "vtcode.exe", "vtcode.cmd" }.Contains(executable, StringComparer.OrdinalIgnoreCase)) return (command, false);
        if (!tokens.Any(t => t.Text == "acp") || command.IndexOfAny([';', '|', '&', '\r', '\n', '`', '$']) >= 0)
            throw new IOException("Cannot verify VT Code authentication for this command. Use a direct vtcode acp command in Settings > Agents > VT Code.");
        string? Option(string name) => tokens.Select((t, i) => t.Text == name ? tokens.ElementAtOrDefault(i + 1).Text : t.Text.StartsWith(name + "=", StringComparison.Ordinal) ? t.Text[(name.Length + 1)..] : null).FirstOrDefault(v => v is not null);
        var provider = Option("--provider"); var key = Option("--api-key-env");
        var openai = provider?.Equals("openai", StringComparison.OrdinalIgnoreCase) == true || provider is null && authenticated?.Contains("openai") == true;
        if (openai && method == "chatgpt" && key is not null && key != "OPENAI_API_KEY")
            throw new IOException("A custom API-key variable overrides ChatGPT authentication in VT Code. Remove --api-key-env or explicitly choose API key in Settings > Agents > VT Code.");
        if (openai) command = command.Insert(tokens[0].End, (provider is null ? " --provider openai" : "") + (key is null ? " --api-key-env OPENAI_API_KEY" : ""));
        // Last CLI override wins over workspace settings. Neither mode allows a
        // silent switch to the other billing source. No credentials are read here.
        return (command + " --config auth.openai.preferred_method=" + method, openai);
    }
}
