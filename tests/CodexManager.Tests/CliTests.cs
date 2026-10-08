using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace CodexManager.Tests;

public class CliTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    // Same configuration as this test build, e.g. src/CodexManager.Cli/bin/Debug/net11.0/vh.dll.
    private static string Vh => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/CodexManager.Cli/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net11.0/vh.dll"));

    private static Task<(int Code, string Output, string Error)> Run(string data, params string[] args) => Run(data, null, args);
    private static async Task<(int Code, string Output, string Error)> Run(string data, Func<Task<string>>? input, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add(Vh); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CODEX_MANAGER_DATA"] = data;
        using var process = Process.Start(start)!;
        if (input is not null) await process.StandardInput.WriteLineAsync(await input());
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (process.ExitCode, await output, await error);
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task CommandLineListsChatsSendsWaitsAndAnswersApprovals()
    {
        var directory = Directory.CreateTempSubdirectory("vh-cli-").FullName;
        Assert.Equal(1, (await Run(directory, "status")).Code);
        using var store = new Store(directory);
        var workspace = new Workspace("w0000000000000000000000000000000", "Fixture", directory); store.Save(workspace);
        var chat = new Chat { Id = "c0ffee00000000000000000000000000", WorkspaceId = workspace.Id, Title = "Fixture chat", Provider = AgentProvider.Codex }; store.Save(chat);
        var command = OperatingSystem.IsWindows() ? $"node '{Fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(Fixture)}";
        var runtimes = new Dictionary<string, ChatRuntime>(); SessionService service = null!;
        ChatRuntime Runtime(Chat c, Workspace w)
        {
            if (runtimes.TryGetValue(c.Id, out var existing)) return existing;
            var created = runtimes[c.Id] = new ChatRuntime(c, w, store, command);
            created.Permission = (request, token) => service.Permission(c, request, token);
            return created;
        }
        service = new SessionService(store, [workspace], [chat], Runtime);
        using var stop = new CancellationTokenSource();
        var serving = CliPipe.Serve(store.DirectoryPath, request => Dispatcher.UIThread.InvokeAsync(() => service.Handle(request)), stop.Token);
        try
        {
            var overview = await Run(directory, "status");
            Assert.Equal(0, overview.Code); Assert.Contains("1 workspaces, 1 chats", overview.Output);
            Assert.Contains("Fixture", (await Run(directory, "workspaces")).Output);
            var chats = await Run(directory, "chats");
            Assert.Contains("c0ffee00  ready", chats.Output); Assert.Contains("Fixture / Fixture chat", chats.Output);

            // Send and wait prints only the reply, with no streaming.
            var sent = await Run(directory, "send", "--wait", "fixture chat", "hello");
            Assert.True(sent.Code == 0, sent.Error);
            Assert.Contains("[assistant]", sent.Output); Assert.Contains("Hello **world**", sent.Output); Assert.DoesNotContain("[user]", sent.Output);
            var shown = await Run(directory, "show", "c0ffee", "-n", "5");
            Assert.Contains("[user]\nhello", shown.Output.ReplaceLineEndings("\n")); Assert.Contains("Hello **world**", shown.Output);

            // A permission request ends the wait with code 3 and says how to answer.
            var asking = await Run(directory, "send", "c0ffee", "permission", "--wait");
            Assert.Equal(3, asking.Code);
            Assert.Contains("Approval needed: Test command", asking.Output); Assert.Contains("allow  Allow once", asking.Output);
            Assert.Contains("vh approve c0ffee00 OPTION", asking.Output);
            Assert.Contains("Approval needed in c0ffee00", (await Run(directory, "status")).Output);
            var approved = await Run(directory, "approve", "c0ffee", "Allow once");
            Assert.True(approved.Code == 0, approved.Error); Assert.Contains("Answered: Allow once", approved.Output);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (chat.Busy && DateTime.UtcNow < until) await Task.Delay(50);
            Assert.Contains("allow", (await Run(directory, "show", "c0ffee", "-n", "1")).Output);

            var listed = JsonNode.Parse((await Run(directory, "--json", "chats")).Output)!.AsArray();
            Assert.Equal("c0ffee00000000000000000000000000", Assert.Single(listed)!["id"]!.GetValue<string>());
            var missing = await Run(directory, "show", "nothing-like-this");
            Assert.Equal(1, missing.Code); Assert.Contains("No chat matches", missing.Error);
            Assert.Equal(2, (await Run(directory, "frobnicate")).Code);
        }
        finally
        {
            stop.Cancel(); await serving;
            foreach (var runtime in runtimes.Values) await runtime.DisposeAsync();
        }
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task CommandLinePairsWithAComputerAndDrivesItsChats()
    {
        var host = Directory.CreateTempSubdirectory("vh-host-").FullName; var client = Directory.CreateTempSubdirectory("vh-client-").FullName;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var store = new Store(host);
        var workspace = new Workspace("w", "Remote fixture", host); store.Save(workspace);
        var chat = new Chat { Id = "beef0000000000000000000000000000", WorkspaceId = "w", Title = "Remote chat", Provider = AgentProvider.Codex }; store.Save(chat);
        var command = OperatingSystem.IsWindows() ? $"node '{Fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(Fixture)}";
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RemoteServer(Path.Combine(host, "remote"), "127.0.0.1", port, service.Handle, (_, number, _, _) => { code.TrySetResult(number); return Task.CompletedTask; });
        var until = DateTime.UtcNow.AddSeconds(10);
        while (server.Fingerprint is null && DateTime.UtcNow < until) await Task.Delay(25);

        var paired = await Run(client, () => code.Task.WaitAsync(TimeSpan.FromSeconds(20)), "pair", "localhost:" + port, "--name", "Test CLI");
        Assert.True(paired.Code == 0, paired.Output + paired.Error);
        Assert.Contains("Enter the six-digit code", paired.Error);
        var hosts = await Run(client, "hosts");
        Assert.Contains("localhost:" + port, hosts.Output);
        var chats = await Run(client, "--host", "localhost", "chats");
        Assert.True(chats.Code == 0, chats.Error); Assert.Contains("beef0000  ready", chats.Output); Assert.Contains("Remote fixture / Remote chat", chats.Output);
        var sent = await Run(client, "--host", "localhost", "send", "remote chat", "hello", "--wait");
        Assert.True(sent.Code == 0, sent.Error); Assert.Contains("Hello **world**", sent.Output);
        Assert.Equal(0, (await Run(client, "unpair", "localhost")).Code);
        Assert.Contains("No paired computers", (await Run(client, "hosts")).Output);
    }
}
