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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.DecorView.ViewTreeObserver?.AddOnGlobalLayoutListener(this);
    }
    public void OnGlobalLayout() => UpdateInsets();
    private void UpdateInsets()
    {
        if (Content is not MainView view || Window?.DecorView is not { } decor) return;
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
    protected override void OnPause() { (Content as MainView)?.SuspendRemotePresentation(); base.OnPause(); }
    protected override void OnResume() { base.OnResume(); UpdateInsets(); (Content as MainView)?.ResumeRemotePresentation(); }
    protected override void OnDestroy() { Window?.DecorView.ViewTreeObserver?.RemoveOnGlobalLayoutListener(this); (Content as MainView)?.DisposeMobile(); base.OnDestroy(); }
}
