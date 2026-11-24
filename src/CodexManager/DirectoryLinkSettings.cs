using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace CodexManager;

internal static class DirectoryLinkSettings
{
    internal static Control Create(Store store, Action<Action> apply)
    {
        var chosen = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var browse = new Button { Name = "ChooseDirectoryLinkApp", Content = "Choose app…" };
        var reset = new Button { Name = "ResetDirectoryLinkApp", Content = "Use system default" };
        void Refresh()
        {
            var path = store.Setting(FileLinks.DirectoryAppKey);
            chosen.Text = string.IsNullOrEmpty(path) ? "System default" : path;
            reset.IsEnabled = !string.IsNullOrEmpty(path);
        }
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(browse)?.StorageProvider is not { } picker) return;
            try
            {
                var path = await PickApp(picker);
                if (path is not null) apply(() =>
                {
                    if (!FileLinks.IsDirectoryApp(path)) throw new IOException("Choose an installed application.");
                    store.Setting(FileLinks.DirectoryAppKey, path);
                    Refresh();
                });
            }
            catch (Exception error) { apply(() => throw error); }
        };
        reset.Click += (_, _) => apply(() => { store.Setting(FileLinks.DirectoryAppKey, ""); Refresh(); });
        Refresh();
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Folder app for links" }, chosen,
                new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Children = { browse, reset } } }
        };
    }

    private static async Task<string?> PickApp(IStorageProvider picker)
    {
        const string title = "Choose an app to open folder links";
        var files = await picker.OpenFilePickerAsync(new()
        {
            Title = title,
            SuggestedStartLocation = OperatingSystem.IsMacOS() ? await picker.TryGetFolderFromPathAsync("/Applications") : null,
            FileTypeFilter = OperatingSystem.IsWindows() ? [new FilePickerFileType("Applications") { Patterns = ["*.exe"] }]
                : OperatingSystem.IsMacOS() ? [new FilePickerFileType("Applications") { AppleUniformTypeIdentifiers = ["com.apple.application-bundle"] }]
                : [FilePickerFileTypes.All]
        });
        if (files.FirstOrDefault() is not { } file) return null;
        using (file) return file.TryGetLocalPath();
    }
}
