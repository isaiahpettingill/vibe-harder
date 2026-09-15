using System.Text.RegularExpressions;

namespace CodexManager;

public sealed record AgentInstallation(string Message, string Url)
{
    public static AgentInstallation? FromError(AgentProvider provider, Exception error)
    {
        if (Missing(error.Message, "node") || Missing(error.Message, "npm") || Missing(error.Message, "npx"))
            return new(AgentProviders.IsAdditional(provider) ? $"Node is required to run {AgentProviders.Get(provider).Name} ACP" : "Node is required to run Codex/Claude ACP bridges", "https://nodejs.org/en/download");
        var binary = provider switch { AgentProvider.OpenCode => "opencode", AgentProvider.Codex => "codex", AgentProvider.VTCode => "vtcode", AgentProvider.Dirac => "dirac", AgentProvider.Pi => "pi", _ => "claude" };
        if (provider == AgentProvider.Pi && Missing(error.Message, "pi-acp")) return new("Install pi-acp to use Pi ACP", "https://github.com/svkozak/pi-acp");
        if (!Missing(error.Message, binary)) return null;
        return provider switch
        {
            AgentProvider.OpenCode => new("Install opencode to use opencode ACP", "https://opencode.ai/download"),
            AgentProvider.Codex => new("Install Codex to use Codex ACP", "https://developers.openai.com/codex/cli/"),
            AgentProvider.VTCode => new("Install VT Code to use VT Code ACP", "https://github.com/vinhnx/VTCode#installation"),
            AgentProvider.Dirac => new("Install Dirac to use Dirac ACP", "https://dirac.run/"),
            AgentProvider.Pi => new("Install Pi and pi-acp to use Pi ACP", "https://github.com/svkozak/pi-acp"),
            _ => new("Install Claude Code to use Claude Code ACP", "https://code.claude.com/docs/en/quickstart")
        };
    }
    private static bool Missing(string text, string name)
    {
        var binary = Regex.Escape(name) + @"(?:\.exe|\.cmd)?";
        var pattern = @"(?im)(?:\b" + binary + @"\b['""` ]*:\s*(?:command not found|not found|No such file or directory)|command not found:\s*" + binary + @"\b|spawn\s+" + binary + @"\s+ENOENT|['""`]?" + binary + @"['""`]?\s+is not recognized|The term\s+['""`]" + binary + @"['""`].*not recognized|(?:cannot find|could not find|unable to find)\s+(?:the\s+)?" + binary + @"\s+(?:executable|binary))";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
