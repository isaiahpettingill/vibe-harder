using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;
using CodexManager;

internal static partial class Program
{
    private static async Task Main()
    {
        BrowserPlatform.Origin = Origin();
        BrowserPlatform.Read = Read;
        BrowserPlatform.Write = Write;
        BrowserPlatform.Download = Download;
        BrowserPlatform.DownloadUrl = DownloadUrl;
        await AppBuilder.Configure<App>().UseBrowser().StartBrowserAppAsync("out");
    }
    [JSImport("origin", "web")] private static partial string Origin();
    [JSImport("read", "web")] private static partial string? Read(string key);
    [JSImport("write", "web")] private static partial void Write(string key, string value);
    [JSImport("download", "web")] private static partial void Download(string name, byte[] content);
    [JSImport("downloadUrl", "web")] private static partial void DownloadUrl(string url);
    [JSExport]
    public static void VisibilityChanged(bool visible)
    {
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime { MainView: MainView view })
        { if (visible) view.ResumeRemotePresentation(); else view.SuspendRemotePresentation(); }
    }
}
