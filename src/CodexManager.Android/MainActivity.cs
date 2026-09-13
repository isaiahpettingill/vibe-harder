using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace CodexManager.Android;

[Activity(Label = "Vibe Harder", MainLauncher = true, Exported = true, Theme = "@style/VibeHarderTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
public sealed class MainActivity : AvaloniaMainActivity, global::Android.Views.ViewTreeObserver.IOnGlobalLayoutListener
{
    private bool resumePending;
    private BiometricAppLock? appLock;
    private global::Android.Net.ConnectivityManager? connectivity;
    private NetworkObserver? networkObserver;
    private sealed class NetworkObserver(MainActivity owner) : global::Android.Net.ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(global::Android.Net.Network network) => owner.RunOnUiThread(() =>
        {
            owner.resumePending = true;
            if (owner.HasWindowFocus) owner.ResumeConnection();
        });
    }
    private void ResumeConnection()
    {
        if (!resumePending || appLock?.Locked == true || Content is not MainView view) return;
        resumePending = false; view.ResumeRemotePresentation();
    }
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        appLock = new BiometricAppLock(this);
        MobileAppSecurity.Current = appLock;
        Window?.DecorView.ViewTreeObserver?.AddOnGlobalLayoutListener(this);
        connectivity = GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager;
        networkObserver = new NetworkObserver(this);
        connectivity?.RegisterDefaultNetworkCallback(networkObserver);
    }
    public void OnGlobalLayout() => UpdateInsets();
    private void UpdateInsets()
    {
        if (Content is not MainView view || Window?.DecorView is not { } decor) return;
        if (HasWindowFocus) ResumeConnection();
        var insets = global::AndroidX.Core.View.ViewCompat.GetRootWindowInsets(decor);
        var content = decor.FindViewById<global::Android.Views.View>(global::Android.Resource.Id.Content);
        if (insets is null || content is null || content.Height == 0) return;
        var density = Resources?.DisplayMetrics?.Density ?? 1;
        var bars = insets.GetInsets(global::AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | global::AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
        if (bars is null) return;
        var keyboardVisible = insets.IsVisible(global::AndroidX.Core.View.WindowInsetsCompat.Type.Ime());
        using var visible = new global::Android.Graphics.Rect();
        decor.GetWindowVisibleDisplayFrame(visible);
        var location = new int[2]; content.GetLocationOnScreen(location);
        // Android may already resize the content above the IME. Reserve only
        // the portion still covering our viewport, never a cached IME height.
        var occlusion = keyboardVisible ? Math.Max(0, location[1] + content.Height - visible.Bottom) / density : 0;
        view.UpdateNativeMobileInsets(new Thickness(bars.Left / density, bars.Top / density, bars.Right / density, keyboardVisible ? 0 : bars.Bottom / density), occlusion);
    }
    internal void OnAppUnlocked() { resumePending = true; UpdateInsets(); ResumeConnection(); }
    protected override void OnPause() { appLock?.Pause(); (Content as MainView)?.SuspendRemotePresentation(); base.OnPause(); }
    protected override void OnStop() { appLock?.Stop(); base.OnStop(); }
    protected override void OnResume() { base.OnResume(); resumePending = true; appLock?.Resume(); UpdateInsets(); ResumeConnection(); }
    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        if (requestCode == BiometricAppLock.CredentialRequest) appLock?.CredentialResult(resultCode);
        else base.OnActivityResult(requestCode, resultCode, data);
    }
    public override void OnBackPressed()
    {
        if (appLock?.Locked == true) MoveTaskToBack(true);
        else base.OnBackPressed();
    }
    public override void OnWindowFocusChanged(bool hasFocus) { base.OnWindowFocusChanged(hasFocus); if (hasFocus) ResumeConnection(); }
    protected override void OnDestroy()
    {
        appLock?.Dispose();
        if (networkObserver is not null) connectivity?.UnregisterNetworkCallback(networkObserver);
        networkObserver?.Dispose(); networkObserver = null;
        Window?.DecorView.ViewTreeObserver?.RemoveOnGlobalLayoutListener(this); (Content as MainView)?.DisposeMobile(); base.OnDestroy();
    }
}
