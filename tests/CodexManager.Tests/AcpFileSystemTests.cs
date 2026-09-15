using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class AcpFileSystemTests
{
    private static JsonElement Request(string path, string? content = null, int line = 1, int limit = int.MaxValue, string session = "s") => JsonSerializer.SerializeToElement(new { sessionId = session, path, content, line, limit });
    [Fact]
    public async Task WslFileRoundTripWhenExplicitlyRequested()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VIBE_TEST_WSL") is not { } distro) return;
        var path = "/tmp/vibe-acp-files-" + Guid.NewGuid().ToString("N") + "/file.txt";
        var workspace = new Workspace("w", "WSL files", "/tmp", distro);
        var files = new AcpFileSystem(workspace, () => "s");
        await files.Write(Request(path, "ACP WSL write ✓\n"), TestContext.Current.CancellationToken);
        Assert.Equal("ACP WSL write ✓\n", (await files.Read(Request(path), TestContext.Current.CancellationToken))["content"]!.GetValue<string>());
        await files.Write(Request(path, "short"), TestContext.Current.CancellationToken);
        Assert.Equal("short", (await files.Read(Request(path), TestContext.Current.CancellationToken))["content"]!.GetValue<string>());
        var local = FileLinks.Resolve(path, workspace);
        File.Delete(local); Directory.Delete(Path.GetDirectoryName(local)!);
    }
    [Fact]
    public async Task ReadsRangesCreatesFilesAndRejectsStaleWritesAndWrongSessions()
    {
        var directory = Directory.CreateTempSubdirectory("acp-files-").FullName;
        var files = new AcpFileSystem(new Workspace("w", "Files", directory), () => "s");
        var path = Path.Combine(directory, "nested", "file.txt");
        await files.Write(Request(path, "first\r\nsecond\r\nthird"), TestContext.Current.CancellationToken);
        Assert.Equal("second\r\n", (await files.Read(Request(path, line: 2, limit: 1), TestContext.Current.CancellationToken))["content"]!.GetValue<string>());
        await File.WriteAllTextAsync(path, "External edit", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => files.Write(Request(path, "Stale agent edit"), TestContext.Current.CancellationToken));
        Assert.Equal("External edit", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await files.Read(Request(path), TestContext.Current.CancellationToken);
        await files.Write(Request(path, "New edit"), TestContext.Current.CancellationToken);
        Assert.Equal("New edit", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => files.Read(Request(path, session: "other"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => files.Read(Request("relative.txt"), TestContext.Current.CancellationToken));
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => files.Write(Request(path, "must not recreate"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task FileCapabilitiesAndErrorsRoundTripWithoutBlockingTheReader()
    {
        var directory = Directory.CreateTempSubdirectory("acp-rpc-files-").FullName;
        var files = new AcpFileSystem(new Workspace("w", "Files", directory), () => "s");
        const string script = "const rl=require('readline').createInterface({input:process.stdin});let pending;const emit=m=>console.log(JSON.stringify(m));rl.on('line',line=>{const m=JSON.parse(line);if(m.method==='initialize')emit({jsonrpc:'2.0',id:m.id,result:m.params.clientCapabilities});else if(m.method==='probe'){pending=m.id;emit({jsonrpc:'2.0',id:'file-op',method:m.params.op,params:m.params.request});}else if(m.id==='file-op')emit({jsonrpc:'2.0',id:pending,result:m});});";
        await using var client = new AcpClient(Hosts.Info("node", "-e", script)) { ReadTextFile = files.Read, WriteTextFile = files.Write };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var caps = await client.Initialize(timeout.Token);
        Assert.True(caps.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.True(caps.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        async Task<JsonElement> Probe(string op, JsonElement request) => await client.Request("probe", new JsonObject { ["op"] = op, ["request"] = JsonNode.Parse(request.GetRawText()) }, timeout.Token);
        var path = Path.Combine(directory, "file.txt");
        var missing = await Probe("fs/read_text_file", Request(path));
        Assert.Equal(-32000, missing.GetProperty("error").GetProperty("code").GetInt32());
        Assert.True((await Probe("fs/write_text_file", Request(path, "ACP write"))).TryGetProperty("result", out _));
        var read = await Probe("fs/read_text_file", Request(path));
        Assert.Equal("ACP write", read.GetProperty("result").GetProperty("content").GetString());
    }
}
