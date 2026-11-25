namespace CodexManager.Tests;

public class ConfigurationTests
{
    [Trait("Category", "Integration")]
    [Theory]
    [InlineData(false, false, "astra", true)]
    [InlineData(true, true, "terra", false)]
    [InlineData(true, false, "sol", true)]
    public async Task ManagedCodexOffersUltrafastWithoutMakingItTheDefault(bool pinned, bool fullAccess, string model, bool eligible)
    {
        var token = TestContext.Current.CancellationToken;
        using var store = new Store(Directory.CreateTempSubdirectory("codex-ultrafast-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        store.Setting("codexUltrafast", "1"); // Old global preference must not opt a chat in.
        store.Setting(AgentProviders.FullAccessKey, fullAccess ? "1" : "0");
        if (pinned) BundledPackages.Pin(store, workspace, new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.12.0", ["@openai/codex"] = "0.172.0" });
        var package = Path.Combine(store.DirectoryPath, "node_modules", "@openai", "codex", "bin");
        Directory.CreateDirectory(package);
        var bin = Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "node_modules", ".bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(package, "codex.js"), """
            process.stdout.write(JSON.stringify({args: process.argv.slice(2)}) + '\n');
            require('node:readline').createInterface({input: process.stdin}).on('line', line => {
                const input = JSON.parse(line);
                const result = input.method === 'model/list' ? {data: [
                    {id:'astra', serviceTiers:[{id:'ultrafast'}]},
                    {id:'terra', serviceTiers:[{id:'fast'}]},
                    {id:'sol', additionalSpeedTiers:['ultrafast']},
                ]} : input.params;
                process.stdout.write(JSON.stringify({id: input.id, result}) + '\n');
            });
            """, token);
        var adapter = Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "node_modules", "@agentclientprotocol", "codex-acp", "dist"));
        await File.WriteAllTextAsync(Path.Combine(adapter.FullName, "index.js"), $$$"""
            const { spawn } = require('node:child_process');
            const wrapper = process.env.CODEX_PATH;
            const child = process.platform === 'win32'
                ? spawn('"' + wrapper + '" app-server', {shell:true}) : spawn(wrapper, ['app-server']);
            const lines = require('node:readline');
            const read = lines.createInterface({input:child.stdout})[Symbol.asyncIterator]();
            (async () => {
                const args = JSON.parse((await read.next()).value).args;
                child.stdin.write(JSON.stringify({id:1,method:'model/list'}) + '\n');
                await read.next();
                const configOptions = [{id:'model',type:'select',currentValue:'{{{model}}}',options:[]}];
                if ('{{{model}}}' !== 'sol') configOptions.push({id:'fast-mode',type:'boolean',currentValue:false});
                for await (const line of lines.createInterface({input:process.stdin})) {
                    const request = JSON.parse(line);
                    let result;
                    if (request.method === 'session/new') result = {args,configOptions};
                    else if (request.method === 'session/set_config_option') {
                        configOptions[1].currentValue = request.params.value;
                        result = {configOptions};
                    } else {
                        child.stdin.write(JSON.stringify({id:3,method:'turn/start',params:{model:'{{{model}}}',serviceTier:'fast',threadId:request.params?.sessionId ?? 'thread-1'}}) + '\n');
                        result = JSON.parse((await read.next()).value).result;
                    }
                    console.log(JSON.stringify({id:request.id,result}));
                }
                child.stdin.end();
            })().catch(error => { console.error(error); process.exit(1); });
            """, token);
        var command = AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex);
        var launch = AgentProviders.Start(workspace, command, AgentProvider.Codex, profile: store.DirectoryPath);
        var wrapper = launch.Environment["VIBE_HARDER_CODEX_WRAPPER"];
        var start = Hosts.Info("node", Path.Combine(Path.GetDirectoryName(wrapper)!, "codex-ultrafast.cjs"), "--acp");
        start.Environment["PATH"] = bin + Path.PathSeparator + launch.Environment["PATH"];
        start.Environment["VIBE_HARDER_CODEX_WRAPPER"] = wrapper;
        start.Environment["VIBE_HARDER_ULTRAFAST_FULL_ACCESS"] = launch.Environment["VIBE_HARDER_ULTRAFAST_FULL_ACCESS"];
        using var process = System.Diagnostics.Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
        async Task<System.Text.Json.Nodes.JsonNode> Read() => System.Text.Json.Nodes.JsonNode.Parse(await process.StandardOutput.ReadLineAsync(limit.Token)
            ?? throw new IOException(await process.StandardError.ReadToEndAsync(limit.Token)))!;
        try
        {
            await process.StandardInput.WriteLineAsync("""{"id":1,"method":"session/new"}""");
            var setup = (await Read())["result"]!;
            var args = setup["args"]!.AsArray().Select(value => value!.GetValue<string>());
            Assert.Equal(fullAccess ? ["--disable", "guardian_approval", AgentProviders.FullAccessArgument, "app-server"] : ["app-server"], args);
            var speed = setup["configOptions"]![1]!;
            Assert.Equal(eligible, speed["options"]?.AsArray().Any(v => v?["value"]?.GetValue<string>() == "ultrafast") == true);
            async Task AssertTier(string tier, string thread = "thread-1")
            {
                await process.StandardInput.WriteLineAsync($$$"""{"id":2,"method":"session/prompt","params":{"sessionId":"{{{thread}}}"}}""");
                Assert.Equal(tier, (await Read())["result"]!["serviceTier"]!.GetValue<string>());
            }
            await AssertTier("fast");
            if (eligible)
            {
                async Task Select(string value)
                {
                    await process.StandardInput.WriteLineAsync($$$"""{"id":3,"method":"session/set_config_option","params":{"configId":"fast-mode","sessionId":"thread-1","value":"{{{value}}}"}}""");
                    Assert.Equal(value, (await Read())["result"]!["configOptions"]![1]!["currentValue"]!.GetValue<string>());
                }
                await Select("ultrafast");
                await AssertTier("ultrafast");
                await AssertTier("fast", "thread-2");
                await Select("off");
                await AssertTier("fast");
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(limit.Token));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Trait("Category", "Integration")]
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task FullAccessCodexProcessStartsWithGuardianDisabled(string autoReview)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-process-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        store.Setting(AgentProviders.AutoReviewKey, autoReview);
        store.Setting(AgentProviders.FullAccessKey, "1");
        var executable = Path.Combine(store.DirectoryPath, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        await File.WriteAllTextAsync(executable, OperatingSystem.IsWindows()
            ? "@echo off\r\nfor %%a in (%*) do echo %%~a\r\n"
            : "#!/bin/sh\nprintf '%s\\n' \"$@\"\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        store.Setting(AgentProviders.CommandKey(AgentProvider.Codex, false), OperatingSystem.IsWindows() ? "& $env:CODEX_PATH app-server" : "\"$CODEX_PATH\" app-server");
        var command = AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex);
        var start = AgentProviders.Start(workspace, command, AgentProvider.Codex);
        start.Environment["PATH"] = store.DirectoryPath + Path.PathSeparator + start.Environment["PATH"];
        var arguments = (await Hosts.Capture(start)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "--disable", "guardian_approval", "--dangerously-bypass-approvals-and-sandbox", "app-server" }, arguments);
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData(null, false, false, "workspace-write")]
    [InlineData(null, null, false, "workspace-write")]
    [InlineData("0", false, false, "workspace-write")]
    [InlineData("1", false, true, "agent")]
    [InlineData("0", true, false, "agent-full-access")]
    [InlineData("1", true, false, "agent-full-access")]
    public void CodexLaunchDisablesGuardianUnlessExplicitlyEnabled(string? setting, bool? fullAccess, bool guardian, string mode)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-guardian-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        if (setting is not null) store.Setting(AgentProviders.AutoReviewKey, setting);
        if (fullAccess is not null) store.Setting(AgentProviders.FullAccessKey, fullAccess.Value ? "1" : "0");
        var command = AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex);
        var start = AgentProviders.Start(workspace, command, AgentProvider.Codex);
        using var config = System.Text.Json.JsonDocument.Parse(start.Environment["CODEX_CONFIG"]!);
        Assert.Equal(guardian, config.RootElement.GetProperty("features").GetProperty("guardian_approval").GetBoolean());
        Assert.Equal(guardian ? "auto_review" : "user", config.RootElement.GetProperty("approvals_reviewer").GetString());
        Assert.Equal(mode, start.Environment["INITIAL_AGENT_MODE"]);
        Assert.DoesNotContain("--codex-auto-review", start.ArgumentList.Last());
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData(null)]
    [InlineData("Debian")]
    public void CodexBridgeUpgradesSavedShippedDefaultButPreservesCustomCommands(string? distro)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-command-").FullName);
        var workspace = new Workspace("w", "Test", "/tmp", distro);
        var key = AgentProviders.CommandKey(AgentProvider.Codex, workspace.IsWsl);
        store.Setting(key, "npx -y @agentclientprotocol/codex-acp@1.11.0");
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        Assert.Equal(Hosts.DefaultAdapter, store.Setting(key));
        const string custom = "my-wrapper npx -y @agentclientprotocol/codex-acp@1.11.0 --custom";
        store.Setting(key, custom);
        Assert.Equal(custom, AgentProviders.Command(store, workspace, AgentProvider.Codex));
    }

    [Trait("Category", "CI")]
    [Fact]
    public void CodexAdapterRunsTheLatestCodexInsteadOfItsPinnedCopy()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("codex-latest-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var key = AgentProviders.CommandKey(AgentProvider.Codex, false);
        store.Setting(key, "npx -y @agentclientprotocol/codex-acp@latest");
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        store.Setting(AgentProviders.FullAccessKey, "0");
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex));
        store.Setting(AgentProviders.FullAccessKey, "1");
        Assert.EndsWith("--dangerously-bypass-approvals-and-sandbox", AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex));
        store.Setting(AgentProviders.FullAccessKey, "0");
        Assert.True(File.Exists(AgentProviders.Start(workspace, Hosts.DefaultAdapter, AgentProvider.Codex, profile: store.DirectoryPath).Environment["VIBE_HARDER_CODEX_WRAPPER"]));
        // A custom launcher without its own Codex package must keep the adapter's bundled binary.
        Assert.False(AgentProviders.Start(workspace, "my-wrapper codex-acp", AgentProvider.Codex).Environment.ContainsKey("CODEX_PATH"));
        // Launches run the pinned set once the updater has installed it; the settings field does not.
        BundledPackages.Pin(store, workspace, new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.0.0", ["@openai/codex"] = "0.159.1" });
        var pinned = AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex);
        Assert.Equal("npx -y --prefer-offline --package=@agentclientprotocol/codex-acp@2.0.0 --package=@openai/codex@0.159.1 -- codex-acp", pinned);
        Assert.True(File.Exists(AgentProviders.Start(workspace, pinned, AgentProvider.Codex, profile: store.DirectoryPath).Environment["VIBE_HARDER_CODEX_WRAPPER"]));
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.Command(store, workspace, AgentProvider.Codex));
        Assert.Equal("npx -y --package=@agentclientprotocol/codex-acp@2.0.0 --package=@openai/codex@0.159.1 -- codex --version",
            BundledPackages.Install(AgentProvider.Codex, new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.0.0", ["@openai/codex"] = "0.159.1" }));
        // Pins apply per environment and never to custom commands.
        Assert.Equal(Hosts.DefaultAdapter, AgentProviders.LaunchCommand(store, new Workspace("d", "WSL", "/tmp", "Debian"), AgentProvider.Codex));
        store.Setting(key, "my-wrapper codex-acp");
        Assert.Equal("my-wrapper codex-acp", AgentProviders.LaunchCommand(store, workspace, AgentProvider.Codex));
    }

    [Trait("Category", "CI")]
    [Fact]
    public void WindowsNpxUsesCmdWithoutChangingExecutionPolicyOrWslCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        var local = new Workspace("w", "Local", Path.GetTempPath());
        var command = "npx -y example login";
        Assert.Equal("npx.cmd -y example login", Hosts.Agent(local, command).ArgumentList.Last());
        Assert.Equal("npx.cmd -y example login", TerminalSession.Options(local, command).CommandLine.Last());
        Assert.Equal("custom-wrapper npx login", Hosts.Agent(local, "custom-wrapper npx login").ArgumentList.Last());
        var wsl = new Workspace("w", "Linux", "/tmp", "Debian");
        Assert.EndsWith("exec " + command, Hosts.Agent(wsl, command).ArgumentList.Last());
    }
    [Trait("Category", "CI")]
    [Fact]
    public void WslResolutionUsesLinuxOverridesAndKeepsDefaultOpenCodeLayer()
    {
        var files = ConfigurationFiles.Candidates(new Dictionary<string, string?>
        {
            ["HOME"] = "/home/test",
            ["USERPROFILE"] = "C:\\Users\\wrong",
            ["CODEX_HOME"] = "/settings/codex",
            ["CLAUDE_CONFIG_DIR"] = "/settings/claude",
            ["XDG_CONFIG_HOME"] = "/xdg",
            ["OPENCODE_CONFIG_DIR"] = "/custom/opencode",
            ["OPENCODE_CONFIG"] = "config/custom.jsonc",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1"
        }, "Debian", "/repo");
        Assert.Contains(files, f => f.Path == "/settings/codex/AGENTS.override.md");
        Assert.Contains(files, f => f.Path == "/settings/claude/settings.json");
        Assert.Contains(files, f => f.Path == "/xdg/opencode/opencode.json");
        Assert.Contains(files, f => f.Path == "/custom/opencode/opencode.jsonc");
        Assert.Contains(files, f => f.Path == "/repo/config/custom.jsonc");
        Assert.DoesNotContain(files, f => f.Title.Contains("fallback"));
        var start = ConfigurationFiles.EditorStartInfo(files[0]);
        Assert.True(start.UseShellExecute); Assert.Equal("open", start.Verb);
        Assert.StartsWith(@"\\wsl.localhost\Debian\settings\codex\", start.FileName);
        Assert.Empty(start.ArgumentList);
    }
    [Trait("Category", "CI")]
    [Fact]
    public void RelativeXdgIsIgnoredAndHomeFallbacksAreUsed()
    {
        var files = ConfigurationFiles.Candidates(new Dictionary<string, string?> { ["HOME"] = "/home/test", ["XDG_CONFIG_HOME"] = "relative" }, "Ubuntu", "/repo");
        Assert.Contains(files, f => f.Path == "/home/test/.config/opencode/opencode.json");
        Assert.Contains(files, f => f.Path == "/home/test/.codex/config.toml");
        Assert.Contains(files, f => f.Path == "/home/test/.claude/CLAUDE.md");
        Assert.Contains(files, f => f.Path == "/home/test/.claude.json");
    }
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("opencode auth login")]
    [InlineData("claude auth login")]
    [InlineData("codex login")]
    public void ProviderLoginCommandsAndWslTerminalArgumentsStaySeparate(string command)
    {
        var workspace = new Workspace("w", "Login", "/home/test/a b", "Debian");
        if (!OperatingSystem.IsWindows()) return;
        var options = TerminalSession.Options(workspace, command);
        Assert.Contains("--exec", options.CommandLine);
        Assert.EndsWith("exec " + command + "\"", options.CommandLine.Last());
        Assert.Contains(Hosts.WindowsArgument(workspace.Path), options.CommandLine);
    }
}
