namespace CodexManager.Tests;

public class TerminalFileLinkTests
{
    [Trait("Category", "CI")]
    [Theory]
    [InlineData("See '/tmp/a,;'", "/tmp/a,;", "/tmp/a,;")]
    [InlineData("See '/tmp/my file.cs:12'", "/tmp/my file.cs:12", "/tmp/my file.cs")]
    [InlineData("See (/tmp/file(v2))", "/tmp/file(v2)", "/tmp/file(v2)")]
    [InlineData("/tmp/file.c:32:7: error", "/tmp/file.c:32:7", "/tmp/file.c")]
    [InlineData("/tmp/file.cs(12,4): error", "/tmp/file.cs(12,4)", "/tmp/file.cs")]
    public void DiagnosticLinksPreserveFilenamesAndUnderlineTheLocation(string text, string span, string path)
    {
        var link = Assert.Single(TerminalFileLinks.Find(text, new Workspace("w", "Test", "/tmp", "Debian")));
        Assert.Equal(span, text.Substring(link.Start, link.Length));
        Assert.Equal(@"\\wsl.localhost\Debian" + path.Replace('/', '\\'), link.Uri.LocalPath);
    }

    [Trait("Category", "CI")]
    [Fact]
    public void RemoteFilesAreNotLinks()
    {
        Assert.Empty(TerminalFileLinks.Find("/home/me/main.cs C:\\repo\\main.cs", null));
    }

    [Trait("Category", "CI")]
    [Fact]
    public void WslFilesUseTheirDistributionNetworkPathAndStripLocations()
    {
        var workspace = new Workspace("w", "Test", "/home/me", "Debian");
        var link = Assert.Single(TerminalFileLinks.Find("error: /home/me/main.cs:12:3", workspace));
        Assert.Equal(@"\\wsl.localhost\Debian\home\me\main.cs", link.Uri.LocalPath);
        Assert.Equal("/home/me/main.cs:12:3", "error: /home/me/main.cs:12:3".Substring(link.Start, link.Length));
        Assert.Empty(TerminalFileLinks.Find("https://example.com/file.cs", workspace));
    }

    [Trait("Category", "CI")]
    [Fact]
    public void QuotedWindowsPathsKeepSpaces()
    {
        var workspace = new Workspace("w", "Test", @"C:\repo");
        var link = Assert.Single(TerminalFileLinks.Find("\"C:\\my repo\\main.cs\"", workspace));
        Assert.Equal(@"C:\my repo\main.cs", link.Uri.LocalPath);
    }
}
