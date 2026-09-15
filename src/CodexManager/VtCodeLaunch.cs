using System.Text.RegularExpressions;

namespace CodexManager;

public static class VtCodeLaunch
{
    public static string WithAuthentication(string command, IReadOnlySet<string>? authenticated)
    {
        if (authenticated?.Contains("openai") != true) return command;
        var tokens = Regex.Matches(command, "\"[^\"]*\"|'[^']*'|[^\\s]+")
            .Select(m => (Text: m.Value.Trim('\"', '\''), End: m.Index + m.Length)).ToArray();
        if (tokens.Length < 2) return command;
        var executable = tokens[0].Text.Replace('\\', '/').Split('/')[^1];
        if (!new[] { "vtcode", "vtcode.exe", "vtcode.cmd" }.Contains(executable, StringComparer.OrdinalIgnoreCase) || !tokens.Any(t => t.Text == "acp")) return command;
        // Shell wrappers and explicit credential overrides remain under the user's control.
        if (command.IndexOfAny([';', '|', '&', '\r', '\n']) >= 0 || tokens.Any(t => t.Text == "--api-key-env" || t.Text.StartsWith("--api-key-env=", StringComparison.Ordinal))) return command;
        var provider = tokens.Select((t, i) => t.Text == "--provider" ? tokens.ElementAtOrDefault(i + 1).Text : t.Text.StartsWith("--provider=", StringComparison.Ordinal) ? t.Text[11..] : null).FirstOrDefault(v => v is not null);
        if (provider is not null && !provider.Equals("openai", StringComparison.OrdinalIgnoreCase)) return command;
        // VT Code's ACP provider picker does not reload OAuth credentials. Load the
        // OpenAI auth handle at process startup, before ACP chooses the session route.
        return command.Insert(tokens[0].End, (provider is null ? " --provider openai" : "") + " --api-key-env OPENAI_API_KEY");
    }
}
