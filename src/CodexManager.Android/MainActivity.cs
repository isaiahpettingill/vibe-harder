using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace CodexManager.Android;

[Activity(Label = "Vibe Harder", MainLauncher = true, Exported = true, Theme = "@style/VibeHarderTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnPause() { (Content as MainView)?.SuspendRemotePresentation(); base.OnPause(); }
    protected override void OnResume() { base.OnResume(); (Content as MainView)?.ResumeRemotePresentation(); }
    protected override void OnDestroy() { (Content as MainView)?.DisposeMobile(); base.OnDestroy(); }
}
