using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class DesktopUpdaterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateResumeIsOneTimeAndDoesNotChangePreference(bool updated)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("update-resume-").FullName);
        store.Setting("autoResume", "0"); store.Setting("updateResume", "a\nb");
        var chats = DesktopUpdater.ConsumeResumeChats(store, updated);
        Assert.Equal(updated ? 2 : 0, chats.Count);
        Assert.Empty(DesktopUpdater.ConsumeResumeChats(store, true));
        Assert.Equal("0", store.Setting("autoResume"));
    }
    private static JsonObject Release(string runtime = "win-x64", string mode = "aot")
    {
        var suffix = runtime.StartsWith("win") ? "-Setup.exe" : runtime.StartsWith("linux") ? ".tar.gz" : ".zip";
        var name = $"VibeHarder-1.2.3-{runtime}-{mode}{suffix}";
        return new JsonObject
        {
            ["tag_name"] = "v1.2.3",
            ["draft"] = false,
            ["prerelease"] = false,
            ["assets"] = new JsonArray(new JsonObject
            {
                ["name"] = name,
                ["size"] = 3L,
                ["digest"] = "sha256:" + new string('a', 64),
                ["browser_download_url"] = $"https://github.com/isaiahpettingill/vibe-harder/releases/download/v1.2.3/{name}"
            })
        };
    }

    [Theory]
    [InlineData("win-x64", "aot")]
    [InlineData("win-arm64", "bundled")]
    [InlineData("linux-x64", "framework")]
    [InlineData("linux-musl-arm64", "aot")]
    [InlineData("osx-arm64", "bundled")]
    public void SelectsOnlyNewMatchingStablePackages(string runtime, string mode)
    {
        var release = Release(runtime, mode);
        Assert.NotNull(DesktopUpdater.SelectRelease(release, new Version(1, 2, 2), runtime, mode));
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 2, 3), runtime, mode));
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 3, 0), runtime, mode));
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 0, 0), "android-arm64", mode));
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 0, 0), runtime, "other"));
        release["prerelease"] = true;
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 0, 0), runtime, mode));
        release["prerelease"] = false; release["draft"] = true;
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 0, 0), runtime, mode));
    }

    [Theory]
    [InlineData("digest", "sha256:wrong")]
    [InlineData("browser_download_url", "https://example.com/update.exe")]
    [InlineData("browser_download_url", "http://github.com/isaiahpettingill/vibe-harder/releases/download/v1.2.3/test.exe")]
    [InlineData("name", "VibeHarder-1.2.3-win-arm64-aot-Setup.exe")]
    public void RejectsUnverifiedOrMismatchedAssets(string key, string value)
    {
        var release = Release(); release["assets"]![0]![key] = value;
        Assert.Null(DesktopUpdater.SelectRelease(release, new Version(1, 0, 0), "win-x64", "aot"));
    }

    private sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    [InlineData(false, 4)]
    public async Task DownloadVerifiesHashAndLengthAndRemovesPartialFiles(bool corruptHash, int publishedSize)
    {
        byte[] bytes = [1, 2, 3];
        using var http = new HttpClient(new DownloadHandler(bytes));
        var root = Directory.CreateTempSubdirectory("update-test-").FullName;
        try
        {
            var release = new DesktopRelease(new Version(1, 2, 3), "update.exe", new Uri("https://example.test/update"),
                corruptHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)), publishedSize);
            if (corruptHash || publishedSize != 3)
                await Assert.ThrowsAsync<IOException>(() => DesktopUpdater.Download(release, TestContext.Current.CancellationToken, http, root));
            else
            {
                var file = await DesktopUpdater.Download(release, TestContext.Current.CancellationToken, http, root);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
            }
            Assert.Empty(Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallerQuotesPathsAsLiteralData()
    {
        Assert.Equal("'a''b$()'", DesktopUpdater.PowerShellQuote("a'b$()"));
        Assert.Equal("'a'\"'\"'b$()'", DesktopUpdater.ShellQuote("a'b$()"));
        var script = DesktopUpdater.UnixInstallScript(123, "/tmp/app new", "/tmp/app old", "/tmp/app old/VibeHarder", "/tmp/log");
        Assert.Contains("kill -0 123", script);
        Assert.Contains("mv '/tmp/app new.previous' '/tmp/app old'", script);
        Assert.Contains("sh '/tmp/app old/install.sh' --refresh-icons || true", script);
        Assert.EndsWith("exec '/tmp/app old/VibeHarder' --updated\n", script);
    }
}
