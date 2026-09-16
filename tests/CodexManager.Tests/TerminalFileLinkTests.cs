namespace CodexManager.Tests;

public class TerminalFileLinkTests
{
    [Fact]
    public void RemoteFilesAreNotLinks()
    {
        Assert.Empty(TerminalFileLinks.Find("/home/me/main.cs C:\\repo\\main.cs", null));
    }

    [Fact]
    public void WslFilesUseTheirDistributionNetworkPathAndStripLocations()
    {
        var workspace = new Workspace("w", "Test", "/home/me", "Debian");
        var link = Assert.Single(TerminalFileLinks.Find("error: /home/me/main.cs:12:3", workspace));
        Assert.Equal(@"\\wsl.localhost\Debian\home\me\main.cs", link.Uri.LocalPath);
        Assert.Equal("/home/me/main.cs:12:3", "error: /home/me/main.cs:12:3".Substring(link.Start, link.Length));
        Assert.Empty(TerminalFileLinks.Find("https://example.com/file.cs", workspace));
    }

    [Fact]
    public void QuotedWindowsPathsKeepSpaces()
    {
        var workspace = new Workspace("w", "Test", @"C:\repo");
        var link = Assert.Single(TerminalFileLinks.Find("\"C:\\my repo\\main.cs\"", workspace));
        Assert.Equal(@"C:\my repo\main.cs", link.Uri.LocalPath);
    }
}
