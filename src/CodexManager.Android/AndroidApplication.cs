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
        base.OnCreate();
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
