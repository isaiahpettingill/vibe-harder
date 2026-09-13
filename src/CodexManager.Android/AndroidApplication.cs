using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace CodexManager.Android;

[Application]
public sealed class AndroidApplication(nint handle, JniHandleOwnership ownership) : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).LogToTrace();
    public override void OnCreate()
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => AppDiagnostics.Record("Android runtime", e.Exception);
        base.OnCreate();
        FileLinks.CacheDirectory = Path.Combine(CacheDir!.AbsolutePath, "linked-files");
        FileLinks.OpenNativeFile = path =>
        {
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".files", new Java.IO.File(path));
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var type = global::Android.Webkit.MimeTypeMap.Singleton!.GetMimeTypeFromExtension(extension) ?? "application/octet-stream";
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, type);
            intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission | global::Android.Content.ActivityFlags.NewTask);
            intent.ClipData = global::Android.Content.ClipData.NewRawUri("File", uri);
            try { StartActivity(intent); }
            catch (global::Android.Content.ActivityNotFoundException) { throw new IOException("No installed app can open this file type."); }
            return Task.CompletedTask;
        };
        // Preserve connections from the previous Android client on upgrade.
        using var store = new Store();
        if (store.Setting("androidConnectionsMigrated") == "1") return;
        var preferences = GetSharedPreferences("remote", global::Android.Content.FileCreationMode.Private)!;
        var address = preferences.GetString("address", null);
        var key = System.IO.Path.Combine(FilesDir!.AbsolutePath, "client-key");
        if (address is not null && File.Exists(key) && RemoteSettings.Hosts(store).Count == 0 && int.TryParse(preferences.GetString("port", "2222"), out var port))
            RemoteSettings.SaveHosts(store, [new RemoteHost(address, address, port, key, preferences.GetString("fingerprint", "")!)]);
        if (store.Setting("theme") is null && preferences.GetString("theme", null) is { } theme) store.Setting("theme", theme);
        store.Setting("androidConnectionsMigrated", "1");
    }
}
