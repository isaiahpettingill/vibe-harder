using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace CodexManager.Tests;

public class WebAccessTests
{
    [AvaloniaFact]
    public async Task WebSettingIsOptInAndDoesNotReconfigureNativeConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "web-settings", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var nativeChanges = 0; var webChanges = 0;
        using var settings = new ConnectionSettingsView(store, false, () => { nativeChanges++; return Task.CompletedTask; }, () => { webChanges++; return Task.CompletedTask; });
        var window = new Window { Content = settings }; window.Show();
        try
        {
            var toggle = settings.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "EnableWebAccess");
            Assert.False(toggle.IsChecked); Assert.Equal(0, webChanges);
            toggle.IsChecked = true;
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal("1", store.Setting("webEnabled")); Assert.Equal(1, webChanges); Assert.Equal(0, nativeChanges);
            toggle.IsChecked = false;
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal("0", store.Setting("webEnabled")); Assert.Equal(2, webChanges); Assert.Equal(0, nativeChanges);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task CompletedBundleIsReusedWithoutDownloading()
    {
        var directory = Path.Combine(Path.GetTempPath(), "web-cache", Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(directory, "web", WebAssets.Version, "wwwroot"); Directory.CreateDirectory(Path.Combine(bundle, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"), "test", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(bundle, "_framework", "dotnet.js"), "test", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(bundle, ".complete"), WebAssets.Version, TestContext.Current.CancellationToken);
        Assert.Equal(bundle, await WebAssets.Ensure(directory, _ => Assert.Fail("A completed cache must not download again."), TestContext.Current.CancellationToken));
    }
    [AvaloniaFact]
    public async Task ServesAssetsPairsAuthenticatesAndRevokesBrowser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "web-access", Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(directory, "assets"); Directory.CreateDirectory(Path.Combine(assets, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(assets, "index.html"), "<title>Vibe Harder</title>", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(assets, "_framework", "test.wasm"), [0, 97, 115, 109], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(assets, "_framework", "icu.dat"), [1, 2, 3], TestContext.Current.CancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        string? number = null; var handled = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var server = await WebServer.Start(assets, directory, "127.0.0.1", port,
            request => { handled++; return Task.FromResult<JsonNode?>(request["method"]?.GetValue<string>() == "file/download" ? new JsonObject { ["path"] = Path.Combine(assets, "index.html") } : new JsonObject { ["echo"] = request["method"]!.DeepClone() }); },
            (_, code, _, _) => { number = code; return Task.CompletedTask; }, timeout.Token);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        Assert.Contains("Vibe Harder", await http.GetStringAsync(origin, timeout.Token));
        using var wasm = await http.GetAsync(origin + "/_framework/test.wasm", timeout.Token);
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", wasm.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(origin + "/_framework/icu.dat", timeout.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(origin + "/host.pfx", timeout.Token)).StatusCode);
        ClientWebSocket Socket(string from)
        {
            var socket = new ClientWebSocket(); socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            socket.Options.SetRequestHeader("Origin", from); return socket;
        }
        var endpoint = new Uri($"wss://127.0.0.1:{port}/remote");
        using (var wrongOrigin = Socket("https://unrelated.example"))
            await Assert.ThrowsAsync<WebSocketException>(() => wrongOrigin.ConnectAsync(endpoint, timeout.Token));
        async Task<JsonObject> Request(ClientWebSocket socket, JsonObject value)
        {
            value["id"] = Guid.NewGuid().ToString("N");
            await WebSocketWire.Write(socket, value, timeout.Token);
            return await WebSocketWire.Read(socket, timeout.Token);
        }
        using (var anonymous = Socket(origin))
        {
            await anonymous.ConnectAsync(endpoint, timeout.Token);
            Assert.NotNull((await Request(anonymous, new() { ["method"] = "list" }))["error"]);
            Assert.Equal(0, handled);
        }
        JsonObject credential;
        using (var pairing = Socket(origin))
        {
            await pairing.ConnectAsync(endpoint, timeout.Token);
            var offer = (await Request(pairing, new() { ["method"] = "pairing/start", ["name"] = "Browser test" }))["result"]!;
            Assert.NotNull(number);
            var srp = new Srp6Client(); srp.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest(), new SecureRandom());
            var value = srp.GenerateClientCredentials(Convert.FromBase64String(offer["salt"]!.GetValue<string>()), Encoding.UTF8.GetBytes("web:" + origin), Encoding.UTF8.GetBytes(number));
            srp.CalculateSecret(new BigInteger(offer["public"]!.GetValue<string>(), 16));
            var reply = (await Request(pairing, new() { ["method"] = "pairing/finish", ["public"] = value.ToString(16), ["proof"] = srp.CalculateClientEvidenceMessage().ToString(16) }))["result"]!;
            Assert.True(srp.VerifyServerEvidenceMessage(new BigInteger(reply["proof"]!.GetValue<string>(), 16)));
            credential = reply["credential"]!.AsObject();
        }
        using var authenticated = Socket(origin); await authenticated.ConnectAsync(endpoint, timeout.Token);
        var login = credential.DeepClone().AsObject(); login["method"] = "auth";
        Assert.True((await Request(authenticated, login))["result"]!.GetValue<bool>());
        Assert.Equal("list", (await Request(authenticated, new() { ["method"] = "list" }))["result"]!["echo"]!.GetValue<string>());
        var download = (await Request(authenticated, new() { ["method"] = "file/download" }))["result"]!["url"]!.GetValue<string>();
        using var downloaded = await http.GetAsync(origin + download, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Vibe Harder", await downloaded.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(origin + download, timeout.Token)).StatusCode);
        var revokedDownload = (await Request(authenticated, new() { ["method"] = "file/download" }))["result"]!["url"]!.GetValue<string>();
        RemoteTrust.Revoke(directory, credential["device"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(origin + revokedDownload, timeout.Token)).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(async () => await WebSocketWire.Read(authenticated, timeout.Token));
        Assert.Equal(3, handled);
    }
    [Theory]
    [InlineData("../escape.js")]
    [InlineData("/escape.js")]
    [InlineData("sub\\escape.js")]
    [InlineData("file:stream")]
    public void RejectsArchivePathsOutsideBundle(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "web-archive", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "bundle.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) zip.CreateEntry(name);
        Assert.Throws<IOException>(() => WebAssets.Extract(file, Path.Combine(directory, "output"), TestContext.Current.CancellationToken));
    }
    [Fact]
    public void RequiresExactReleaseAndChecksum()
    {
        var release = new JsonObject { ["tag_name"] = "v1.2.3", ["assets"] = new JsonArray(new JsonObject { ["name"] = WebAssets.ArchiveName, ["size"] = 123L, ["digest"] = "sha256:" + new string('a', 64), ["browser_download_url"] = "https://github.com/isaiahpettingill/vibe-harder/releases/download/v1.2.3/VibeHarder-Web.zip" }) };
        Assert.Equal("1.2.3", WebAssets.SelectRelease(release, "1.2.3").Version.ToString());
        Assert.Throws<IOException>(() => WebAssets.SelectRelease(release, "1.2.4"));
        release["assets"]![0]!["digest"] = "";
        Assert.Throws<IOException>(() => WebAssets.SelectRelease(release, "1.2.3"));
    }
}
