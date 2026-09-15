using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexManager;

// Probe on the agent's host. Only provider identifiers leave the credential reader.
public static class ProviderAuthentication
{
    public static async Task<HashSet<string>?> Read(Workspace workspace, AgentProvider agent)
    {
        try
        {
            if (agent == AgentProvider.VTCode)
            {
                var auth = Hosts.Capture(Hosts.Agent(workspace, "vtcode auth --no-color"), TimeSpan.FromSeconds(10));
                var keys = Hosts.Capture(Hosts.Agent(workspace, "vtcode secret list --no-color"), TimeSpan.FromSeconds(10));
                await Task.WhenAll(auth, keys);
                return ReadVtCode(await auth, await keys);
            }
            if (agent != AgentProvider.Dirac) return null;
            var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(DiracProbe));
            var output = await Hosts.Capture(Hosts.Agent(workspace, "node -e \"eval(Buffer.from('" + script + "','base64').toString())\""), TimeSpan.FromSeconds(10));
            using var json = JsonDocument.Parse(output);
            return json.RootElement.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; } // An unavailable status command is not evidence of missing credentials.
    }
    public static HashSet<string> ReadVtCode(string auth, string keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, id) in new[] { ("OpenAI", "openai"), ("OpenRouter", "openrouter"), ("GitHub Copilot", "copilot"), ("Codex", "codex") })
            if (auth.Split('\n').Any(line => line.StartsWith(label + ": authenticated", StringComparison.OrdinalIgnoreCase) || line.StartsWith(label + ": using ", StringComparison.OrdinalIgnoreCase))) result.Add(id);
        foreach (Match block in Regex.Matches(keys, @"(?m)^  [^\r\n]+\(([a-z0-9-]+)\)\r?\n    Status: Ready\r?\n    Source: ([^\r\n]+)"))
            if (block.Groups[2].Value is not "Managed auth (external CLI)" and not "Local — no key required") result.Add(block.Groups[1].Value);
        return result;
    }
    public static IReadOnlyList<SessionConfig> Filter(IReadOnlyList<SessionConfig> options, HashSet<string>? authenticated) => authenticated is null ? options : options.Select(option =>
        option.Id == "provider" ? option with { Values = option.Values.Where(value => authenticated.Contains(value.Value)).ToArray() } : option).ToArray();

    // Dirac 0.5.x CLI storage layout and provider-key mapping. Do not print secrets or account details.
    private const string DiracProbe = """
        const fs = require('fs'), path = require('path'), os = require('os');
        const root = process.env.DIRAC_DATA_DIR || path.join(process.env.DIRAC_DIR || path.join(os.homedir(), '.dirac'), 'data');
        function read(name) { try { return JSON.parse(fs.readFileSync(path.join(root, name), 'utf8')); } catch (e) { if (e.code === 'ENOENT') return {}; throw e; } }
        const config = {...read('globalState.json'), ...read('secrets.json')};
        const fields = {
          anthropic:['apiKey'], openrouter:['openRouterApiKey'], openai:['openAiApiKey','openAiCompatibleCustomApiKey'],
          'openai-native':['openAiNativeApiKey'], 'openai-codex':['openai-codex-oauth-credentials'],
          'github-copilot':['github-copilot-oauth-credentials'], gemini:['geminiApiKey'], requesty:['requestyApiKey'],
          together:['togetherApiKey'], deepseek:['deepSeekApiKey'], qwen:['qwenApiKey'], 'qwen-code':['qwenCodeOauthPath'],
          doubao:['doubaoApiKey'], mistral:['mistralApiKey'], litellm:['liteLlmApiKey'], moonshot:['moonshotApiKey'],
          nebius:['nebiusApiKey'], fireworks:['fireworksApiKey'], xai:['xaiApiKey'], sambanova:['sambanovaApiKey'],
          cerebras:['cerebrasApiKey'], groq:['groqApiKey'], huggingface:['huggingFaceApiKey'],
          'huawei-cloud-maas':['huaweiCloudMaasApiKey'], dify:['difyApiKey'], baseten:['basetenApiKey'],
          'vercel-ai-gateway':['vercelAiGatewayApiKey'], zai:['zaiApiKey'], aihubmix:['aihubmixApiKey'],
          minimax:['minimaxApiKey'], nousResearch:['nousResearchApiKey'], wandb:['wandbApiKey']
        };
        const ids = Object.entries(fields).filter(([id, keys]) => keys.some(key => typeof config[key] === 'string' ? config[key].trim().length > 0 : !!config[key])).map(([id]) => id);
        const envKeys = {'openai-native':'OPENAI_API_KEY',anthropic:'ANTHROPIC_API_KEY',openrouter:'OPENROUTER_API_KEY',gemini:'GEMINI_API_KEY',deepseek:'DEEPSEEK_API_KEY',groq:'GROQ_API_KEY',xai:'XAI_API_KEY',mistral:'MISTRAL_API_KEY'};
        for (const [id,key] of Object.entries(envKeys)) if (process.env[key]?.trim()) ids.push(id);
        if (config.openAiCompatibleProfiles?.some(p => p.apiKey)) ids.push('openai');
        if (config.awsBedrockApiKey || (config.awsAccessKey && config.awsSecretKey) || (config.awsUseProfile && config.awsProfile)) ids.push('bedrock');
        if (config.vertexProjectId || config.geminiApiKey) ids.push('vertex');
        console.log(JSON.stringify([...new Set(ids)]));
        """;
}
