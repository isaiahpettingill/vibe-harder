using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace CodexManager.Android;

// UnifiedPush connector (AND_3 spec, https://unifiedpush.org/developers/spec/android/) written
// against the protocol rather than one distributor, so any installed distributor works (Sunup,
// ntfy, NextPush, ...). Each paired computer has its own registration, because each computer signs
// its pushes with its own VAPID key. State lives in SharedPreferences so broadcast receivers can
// use it without the app's UI.
internal sealed class AndroidUnifiedPush : IMobilePush
{
    internal const string ActionRegister = "org.unifiedpush.android.distributor.REGISTER";
    internal const string ActionUnregister = "org.unifiedpush.android.distributor.UNREGISTER";
    internal const string ActionAck = "org.unifiedpush.android.distributor.MESSAGE_ACK";
    internal const string ActionNewEndpoint = "org.unifiedpush.android.connector.NEW_ENDPOINT";
    internal const string ActionMessage = "org.unifiedpush.android.connector.MESSAGE";
    internal const string ActionUnregistered = "org.unifiedpush.android.connector.UNREGISTERED";
    internal const string ActionFailed = "org.unifiedpush.android.connector.REGISTRATION_FAILED";
    internal const string ActionRaise = "org.unifiedpush.android.connector.RAISE_TO_FOREGROUND";
    internal const int LinkRequest = 9303;
    private const string Channel = "agent-completions";

    private static AndroidUnifiedPush? instance;
    public static AndroidUnifiedPush For(Context context) => instance ??= new AndroidUnifiedPush(context.ApplicationContext!);

    private readonly Context context;
    private readonly ISharedPreferences preferences;
    private readonly object sync = new();
    private string status = "";
    public event Action? Changed;
    // Set by the activity: asks for notification permission and opens the distributor chooser.
    public Func<Task<bool>>? RequestPermission { get; set; }
    public Activity? Activity { get; set; }

    private AndroidUnifiedPush(Context context)
    {
        this.context = context;
        preferences = context.GetSharedPreferences("unifiedpush", FileCreationMode.Private)!;
    }

    public bool Enabled => preferences.GetBoolean("enabled", true);
    public string Status { get { lock (sync) return status; } }
    public bool CanChooseDistributor => Distributors().Count > 1;
    private void Report(string text) { lock (sync) status = text; Changed?.Invoke(); }

    private sealed record Registration(string Token, string PublicKey, string PrivateKey, string Auth, string? Endpoint, bool Synced, string? Distributor)
    {
        public JsonObject ToJson() => new() { ["token"] = Token, ["public"] = PublicKey, ["private"] = PrivateKey, ["auth"] = Auth, ["endpoint"] = Endpoint, ["synced"] = Synced, ["distributor"] = Distributor };
        public static Registration From(JsonNode node) => new(node["token"]!.GetValue<string>(), node["public"]!.GetValue<string>(), node["private"]!.GetValue<string>(), node["auth"]!.GetValue<string>(),
            node["endpoint"]?.GetValue<string>(), node["synced"]?.GetValue<bool>() == true, node["distributor"]?.GetValue<string>());
    }
    private Dictionary<string, Registration> Registrations()
    {
        lock (sync)
        {
            try { return (JsonNode.Parse(preferences.GetString("registrations", "{}")!) as JsonObject ?? []).ToDictionary(p => p.Key, p => Registration.From(p.Value!)); }
            catch (Exception error) when (error is System.Text.Json.JsonException or NullReferenceException or InvalidOperationException) { return []; }
        }
    }
    private void Save(Dictionary<string, Registration> registrations)
    {
        lock (sync) preferences.Edit()!.PutString("registrations", new JsonObject(registrations.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value.ToJson()))).ToJsonString())!.Apply();
    }
    private static List<RemoteHost> Hosts() { using var store = new Store(); return RemoteSettings.Hosts(store).ToList(); }

    // Installed distributors: apps whose receiver handles REGISTER.
    private List<string> Distributors()
    {
        var receivers = context.PackageManager!.QueryBroadcastReceivers(new Intent(ActionRegister), 0) ?? [];
        return receivers.Select(r => r.ActivityInfo?.PackageName).OfType<string>().Where(p => p != context.PackageName).Distinct().ToList();
    }
    private string? Distributor()
    {
        var installed = Distributors();
        var saved = preferences.GetString("distributor", null);
        if (saved is not null && installed.Contains(saved)) return saved;
        // The saved distributor is gone: its registrations are void.
        if (saved is not null) { Save([]); preferences.Edit()!.Remove("distributor")!.Apply(); }
        if (installed.Count == 1) { Use(installed[0]); return installed[0]; }
        if (installed.Count > 1 && Activity is { } activity)
        {
            // First selection goes through the default distributor's link activity (unifiedpush://link).
            try { activity.StartActivityForResult(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse("unifiedpush://link")), LinkRequest); }
            catch (ActivityNotFoundException) { ChooseDistributor(); }
        }
        return null;
    }
    private void Use(string distributor) => preferences.Edit()!.PutString("distributor", distributor)!.Apply();
    private string Label(string package)
    {
        try { return context.PackageManager!.GetApplicationLabel(context.PackageManager.GetApplicationInfo(package, 0)) ?? package; }
        catch (PackageManager.NameNotFoundException) { return package; }
    }

    public void LinkResult(Result result, Intent? data)
    {
        var pi = OperatingSystem.IsAndroidVersionAtLeast(33) ? data?.GetParcelableExtra("pi", Java.Lang.Class.FromType(typeof(PendingIntent))) as PendingIntent : data?.GetParcelableExtra("pi") as PendingIntent;
        if (result == Result.Ok && pi?.CreatorPackage is { } package && Distributors().Contains(package)) { SwitchTo(package); return; }
        ChooseDistributor();
    }
    public void ChooseDistributor()
    {
        var installed = Distributors();
        if (Activity is not { } activity || installed.Count == 0) { Refresh(); return; }
        var labels = installed.Select(Label).ToArray();
        activity.RunOnUiThread(() => new AlertDialog.Builder(activity).SetTitle("Push distributor")!
            .SetItems(labels, (_, e) => SwitchTo(installed[e.Which]))!.SetNegativeButton("Cancel", (_, _) => { })!.Show());
    }
    private void SwitchTo(string distributor)
    {
        var previous = preferences.GetString("distributor", null);
        if (previous is not null && previous != distributor)
            foreach (var registration in Registrations().Values) Send(ActionUnregister, previous, new() { ["token"] = registration.Token });
        if (previous != distributor) Save([]);
        Use(distributor); Refresh();
    }

    public async Task SetEnabled(bool enabled)
    {
        if (enabled && OperatingSystem.IsAndroidVersionAtLeast(33) && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted
            && (RequestPermission is null || !await AskPermission()))
        {
            // Without permission nothing can be shown, so push stays off.
            preferences.Edit()!.PutBoolean("enabled", false)!.Apply();
            Report("Notifications are not allowed for Vibe Harder. Allow them in Android settings to turn this on.");
            return;
        }
        preferences.Edit()!.PutBoolean("enabled", enabled)!.Apply();
        if (enabled) { Refresh(); return; }
        var distributor = preferences.GetString("distributor", null);
        var registrations = Registrations();
        Save([]);
        Report("Push notifications are off.");
        foreach (var (key, registration) in registrations)
        {
            if (distributor is not null) Send(ActionUnregister, distributor, new() { ["token"] = registration.Token });
            if (Hosts().FirstOrDefault(h => MobilePush.Key(h) == key) is { } host) _ = Sync(() => MobilePush.Unregister(host, Timeout()));
        }
    }
    public void HostsChanged() => Refresh();
    private Task<bool> AskPermission() { preferences.Edit()!.PutBoolean("askedPermission", true)!.Apply(); return RequestPermission!(); }

    // Registers every paired computer with the distributor. Repeating a registration is expected:
    // the distributor answers NEW_ENDPOINT again (maybe with a new endpoint), which is synced.
    public void Refresh()
    {
        if (!Enabled) { Report("Push notifications are off."); return; }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
        {
            // Push is on by default: ask once, the first time there is a window to ask from.
            if (!preferences.GetBoolean("askedPermission", false) && RequestPermission is not null) { _ = SetEnabled(true); return; }
            preferences.Edit()!.PutBoolean("enabled", false)!.Apply();
            Report("Notifications are not allowed for Vibe Harder. Allow them in Android settings to turn this on.");
            return;
        }
        if (Distributors().Count == 0) { Report("Install a UnifiedPush distributor app, such as Sunup, to get notifications on this phone."); return; }
        if (Distributor() is not { } distributor) { Report("Choose a push distributor."); return; }
        var hosts = Hosts();
        var registrations = Registrations();
        foreach (var removed in registrations.Keys.Where(key => hosts.All(h => MobilePush.Key(h) != key)).ToArray())
        {
            Send(ActionUnregister, distributor, new() { ["token"] = registrations[removed].Token });
            registrations.Remove(removed);
        }
        Save(registrations);
        Report($"Using {Label(distributor)}." + (hosts.Count == 0 ? " Pair a computer to receive its notifications." : ""));
        foreach (var host in hosts)
        {
            var key = MobilePush.Key(host);
            _ = Sync(async () =>
            {
                var vapid = await MobilePush.VapidKey(host, Timeout());
                var registration = Registrations().GetValueOrDefault(key);
                if (registration is null)
                {
                    var keys = WebPush.Generate();
                    registration = new(Guid.NewGuid().ToString(), WebPush.Base64Url(keys.PublicKey), WebPush.Base64Url(keys.PrivateKey), WebPush.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), null, false, distributor);
                    var all = Registrations(); all[key] = registration; Save(all);
                }
                Send(ActionRegister, distributor, new() { ["token"] = registration.Token, ["vapid"] = vapid, ["message"] = "Vibe Harder: " + host.Name }, identify: true);
            });
        }
    }
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
    private async Task Sync(Func<Task> work)
    {
        try { await Task.Run(work); }
        // The computer may be offline; the next app start or resume tries again.
        catch (Exception error) { AppDiagnostics.Record("Push registration", error); }
    }

    private void Send(string action, string distributor, Dictionary<string, string> extras, bool identify = false)
    {
        var intent = new Intent(action); intent.SetPackage(distributor);
        foreach (var (name, value) in extras) intent.PutExtra(name, value);
        intent.PutExtra("application", context.PackageName);
        // Identifies this app to the distributor: shared identity on Android 14+, a pending intent before.
        intent.PutExtra("pi", PendingIntent.GetBroadcast(context, 0, new Intent("org.unifiedpush.dummy_app"), PendingIntentFlags.Immutable));
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var options = BroadcastOptions.MakeBasic(); options.SetShareIdentityEnabled(true);
            context.SendBroadcast(intent, null, options.ToBundle());
        }
        else context.SendBroadcast(intent);
    }

    // Distributor → app messages, from the exported receiver.
    public void Receive(Intent intent, BroadcastReceiver.PendingResult? pending)
    {
        var token = intent.GetStringExtra("token");
        var registrations = Registrations();
        var (key, registration) = registrations.FirstOrDefault(r => r.Value.Token == token);
        if (token is null || registration is null) { pending?.Finish(); return; }
        var distributor = preferences.GetString("distributor", null);
        void Acknowledge() { if (intent.GetStringExtra("id") is { Length: > 0 } id && distributor is not null) Send(ActionAck, distributor, new() { ["token"] = token, ["id"] = id }); }
        switch (intent.Action)
        {
            case ActionNewEndpoint:
                var endpoint = intent.GetStringExtra("endpoint");
                Acknowledge();
                if (endpoint is null) { pending?.Finish(); return; }
                registration = registration with { Endpoint = endpoint, Synced = registration.Synced && registration.Endpoint == endpoint, Distributor = distributor };
                registrations[key] = registration; Save(registrations);
                var host = Hosts().FirstOrDefault(h => MobilePush.Key(h) == key);
                if (host is null || registration.Synced) { pending?.Finish(); return; }
                var current = registration;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MobilePush.Register(host, endpoint, current.PublicKey, current.Auth, distributor, Timeout());
                        var saved = Registrations();
                        if (saved.TryGetValue(key, out var latest) && latest.Endpoint == endpoint) { saved[key] = latest with { Synced = true }; Save(saved); }
                        Report($"Using {Label(distributor ?? "")}.");
                    }
                    catch (Exception error) { AppDiagnostics.Record("Push registration", error); Report($"Could not reach {host.Name} to set up notifications. It will retry when the app opens."); }
                    finally { pending?.Finish(); }
                });
                return;
            case ActionMessage:
                var bytes = intent.GetByteArrayExtra("bytesMessage");
                Acknowledge();
                if (bytes is not null) Show(key, registration, bytes);
                break;
            case ActionUnregistered:
                registrations.Remove(key); Save(registrations);
                if (Hosts().FirstOrDefault(h => MobilePush.Key(h) == key) is { } removedHost) _ = Sync(() => MobilePush.Unregister(removedHost, Timeout()));
                Report("The push distributor ended a registration. Reopen the app to register again.");
                break;
            case ActionFailed:
                // The spec requires a new token for the next attempt.
                registrations.Remove(key); Save(registrations);
                Report("The push distributor could not register (" + (intent.GetStringExtra("reason") ?? "unknown reason") + ").");
                break;
        }
        pending?.Finish();
    }

    private void Show(string key, Registration registration, byte[] message)
    {
        JsonObject? payload;
        try
        {
            var keys = new WebPush.KeyPair(WebPush.FromBase64Url(registration.PublicKey), WebPush.FromBase64Url(registration.PrivateKey));
            payload = MobilePush.ReadPayload(WebPush.Decrypt(message, keys, WebPush.FromBase64Url(registration.Auth)));
        }
        catch (System.Security.Cryptography.CryptographicException error) { AppDiagnostics.Record("Push message", error); return; }
        if (payload is null) return;
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) manager.CreateNotificationChannel(new NotificationChannel(Channel, "Finished chats", NotificationImportance.High));
        var chat = payload["chat"]?.GetValue<string>();
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        intent.SetAction("push:" + key + ":" + chat); intent.PutExtra("pushHost", key); intent.PutExtra("pushChat", chat);
        var open = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new AndroidX.Core.App.NotificationCompat.Builder(context, Channel)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentTitle(payload["title"]?.GetValue<string>() ?? "Vibe Harder")
            .SetContentText(payload["body"]?.GetValue<string>() ?? "")
            .SetContentIntent(open).SetAutoCancel(true).SetVisibility(-1).Build();
        manager.Notify("push:" + key + ":" + chat, 0, notification);
    }
}

[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter([AndroidUnifiedPush.ActionNewEndpoint, AndroidUnifiedPush.ActionMessage, AndroidUnifiedPush.ActionUnregistered, AndroidUnifiedPush.ActionFailed])]
public sealed class UnifiedPushReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        AndroidUnifiedPush.For(context).Receive(intent, GoAsync());
    }
}

// Distributors may bind here for a few seconds to raise the app to foreground importance.
[Service(Exported = true)]
[IntentFilter([AndroidUnifiedPush.ActionRaise])]
public sealed class RaiseToForegroundService : Service
{
    public override IBinder? OnBind(Intent? intent) => new Binder();
}
