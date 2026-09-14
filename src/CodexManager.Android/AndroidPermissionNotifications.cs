using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace CodexManager.Android;

internal sealed class AndroidPermissionNotifications(MainActivity activity) : IDisposable
{
    internal const int PermissionRequest = 9202;
    private const string Channel = "agent-permissions";
    private readonly Dictionary<string, (string Title, Action Activate)> pending = [];
    private bool requesting;
    public void Show(string id, string title, Action activate)
    {
        pending[id] = (title, activate);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
        {
            if (!requesting) { requesting = true; activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], PermissionRequest); }
            return;
        }
        var manager = (NotificationManager)activity.GetSystemService(Context.NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) manager.CreateNotificationChannel(new NotificationChannel(Channel, "Agent permission requests", NotificationImportance.High));
        var intent = new Intent(activity, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop); intent.SetAction("permission:" + id); intent.PutExtra("permissionId", id);
        var open = PendingIntent.GetActivity(activity, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new AndroidX.Core.App.NotificationCompat.Builder(activity, Channel)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo).SetContentTitle("Permission needed")
            .SetContentText(title).SetContentIntent(open).SetAutoCancel(true).SetVisibility(-1).Build();
        manager.Notify(id, 0, notification);
    }
    public void PermissionResult()
    {
        // Do not repeatedly ask after the user denies system notifications.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Permission.Granted)
            foreach (var (id, item) in pending.ToArray()) Show(id, item.Title, item.Activate);
    }
    public void Activate(Intent? intent)
    {
        if (intent?.GetStringExtra("permissionId") is not { } id || !pending.Remove(id, out var item)) return;
        item.Activate(); intent.RemoveExtra("permissionId");
    }
    public void Dismiss(string id) { pending.Remove(id); ((NotificationManager)activity.GetSystemService(Context.NotificationService)!).Cancel(id, 0); }
    public void Dispose() { pending.Clear(); PermissionNotifications.Mobile = null; PermissionNotifications.MobileDismiss = null; }
}
