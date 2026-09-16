using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace CodexManager.Android;

// Native cover sits above Avalonia, including popups, and is installed before
// the first frame. No credentials or biometric data ever enter the app.
internal sealed class BiometricAppLock : IMobileAppSecurity, IDisposable
{
    internal const int CredentialRequest = 7412;
    private readonly MainActivity activity;
    private readonly ISharedPreferences preferences;
    private readonly FrameLayout cover;
    private readonly TextView message;
    private readonly List<View> hidden = [];
    private CancellationSignal? cancellation;
    private TaskCompletionSource<string>? settingChange;
    private bool? desiredSetting;
    private bool authenticating, credentialPending, resumed, disposed;
    private bool authenticated, automaticPrompt = true;
    private bool enabled;
    private int generation;
    public bool Enabled => enabled;
    public bool Locked { get; private set; }

    public BiometricAppLock(MainActivity activity)
    {
        this.activity = activity;
        preferences = activity.GetSharedPreferences("app-security", FileCreationMode.Private)!;
        enabled = preferences.GetBoolean("enabled", false);
        cover = new FrameLayout(activity) { Clickable = true, Focusable = true, ImportantForAccessibility = ImportantForAccessibility.Yes };
        cover.SetBackgroundColor(global::Android.Graphics.Color.Rgb(30, 30, 46));
        var panel = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        panel.SetPadding(32, 32, 32, 32);
        message = new TextView(activity) { Text = "Vibe Harder is locked", TextSize = 22, Gravity = GravityFlags.Center };
        message.SetTextColor(global::Android.Graphics.Color.White);
        panel.AddView(message);
        var unlock = new Button(activity) { Text = "Unlock" };
        unlock.Click += (_, _) => Authenticate(); panel.AddView(unlock);
        var credential = new Button(activity) { Text = "Use device PIN or password" };
        credential.Click += (_, _) => Authenticate(true); panel.AddView(credential);
        cover.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        activity.AddContentView(cover, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        Locked = Enabled; UpdateCover();
    }

    private void UpdateCover()
    {
        if (Locked)
        {
            if (cover.Parent is ViewGroup parent)
                for (var i = 0; i < parent.ChildCount; i++)
                    if (parent.GetChildAt(i) is { } child && child != cover && child.Visibility == ViewStates.Visible)
                    { hidden.Add(child); child.Visibility = ViewStates.Invisible; }
            cover.Visibility = ViewStates.Visible; cover.BringToFront();
            var keyboard = activity.GetSystemService(Context.InputMethodService) as global::Android.Views.InputMethods.InputMethodManager;
            keyboard?.HideSoftInputFromWindow(cover.WindowToken, 0);
        }
        else
        {
            foreach (var child in hidden) child.Visibility = ViewStates.Visible;
            hidden.Clear(); cover.Visibility = ViewStates.Gone;
        }
    }

    public void Resume()
    {
        resumed = true;
        if (authenticated) { authenticated = false; Unlock(); }
        if (Locked && automaticPrompt && !authenticating) { automaticPrompt = false; Authenticate(); }
    }

    public void Pause()
    {
        resumed = false;
        var pickingFile = MobileAppSecurity.ConsumeFilePickerPause();
        if (Enabled && !pickingFile) { Locked = true; UpdateCover(); }
    }

    public void Stop()
    {
        // A credential activity legitimately stops this activity. All other
        // background transitions invalidate callbacks from the previous prompt.
        if (credentialPending) return;
        automaticPrompt = true; authenticated = false;
        CancelAuthentication("Authentication cancelled.");
    }

    public Task<string> ChangeEnabled(bool enabled)
    {
        if (disposed) return Task.FromResult("Open settings again to change app lock.");
        if (!resumed) return Task.FromResult("Return to the app before changing app lock.");
        if (authenticating || settingChange is not null) return Task.FromResult("Finish the current authentication first.");
        if (Enabled == enabled) return Task.FromResult(enabled ? "App lock is enabled." : "App lock is off.");
        var keyguard = activity.GetSystemService(Context.KeyguardService) as KeyguardManager;
        if (keyguard?.IsDeviceSecure != true) return Task.FromResult("Set up a screen lock and biometrics in Android Settings first.");
        settingChange = new(TaskCreationOptions.RunContinuationsAsynchronously); desiredSetting = enabled;
        var result = settingChange.Task; Authenticate(); return result;
    }

    private void Authenticate(bool deviceCredential = false)
    {
        if (disposed || !resumed || authenticating) return;
        authenticating = true; var attempt = ++generation;
        message.Text = "Unlock Vibe Harder";
        try
        {
            if (deviceCredential || !OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                var manager = activity.GetSystemService(Context.KeyguardService) as KeyguardManager;
                var intent = manager?.CreateConfirmDeviceCredentialIntent("Unlock Vibe Harder", "Confirm your device screen lock");
                if (intent is null) { Finish(attempt, false, "Set up a device screen lock in Android Settings."); return; }
                credentialPending = true;
                activity.StartActivityForResult(intent, CredentialRequest);
                return;
            }
            cancellation = new CancellationSignal();
            var builder = new BiometricPrompt.Builder(activity).SetTitle("Unlock Vibe Harder")!.SetSubtitle("Confirm your identity to access your chats")!;
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) builder.SetAllowedAuthenticators(0x000f | 0x8000); // BIOMETRIC_STRONG | DEVICE_CREDENTIAL
            else if (OperatingSystem.IsAndroidVersionAtLeast(29)) builder.SetDeviceCredentialAllowed(true);
            else builder.SetNegativeButton("Use screen lock", activity.MainExecutor!, new CancelButton(() => UseCredential(attempt)));
            builder.Build()!.Authenticate(cancellation, activity.MainExecutor!, new Authentication(this, attempt));
        }
        catch (Exception error)
        {
            AppDiagnostics.Record("Android app unlock", error);
            Finish(attempt, false, "Could not open authentication. Try your device PIN or password.");
        }
    }

    public void CredentialResult(Result result)
    {
        if (credentialPending) Finish(generation, result == Result.Ok, "Authentication cancelled.");
    }

    private void UseCredential(int attempt)
    {
        if (disposed || attempt != generation || !authenticating) return;
        ++generation; authenticating = false;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        Authenticate(true);
    }

    private void Finish(int attempt, bool success, string error = "Authentication cancelled.")
    {
        if (disposed || attempt != generation || !authenticating) return;
        authenticating = false; credentialPending = false;
        cancellation?.Dispose(); cancellation = null;
        var changed = desiredSetting;
        if (success && changed is { } requested)
        {
            try
            {
                using var editor = preferences.Edit()!;
                if (!editor.PutBoolean("enabled", requested)!.Commit()) throw new IOException("Could not persist app lock.");
                enabled = requested;
            }
            catch (Exception exception)
            {
                AppDiagnostics.Record("Saving Android app lock", exception);
                success = false; error = "Could not save app lock. Your previous setting remains active for this session.";
            }
        }
        settingChange?.TrySetResult(success ? (Enabled ? "Biometric unlock enabled." : "Biometric unlock disabled.") : error);
        settingChange = null; desiredSetting = null;
        if (success)
        {
            if (resumed) Unlock();
            else authenticated = true;
        }
        else message.Text = error + " Tap Unlock to retry, or use your device screen lock.";
    }

    private void Unlock()
    {
        var wasLocked = Locked;
        Locked = false; UpdateCover();
        if (wasLocked) activity.OnAppUnlocked();
    }

    private void CancelAuthentication(string reason)
    {
        ++generation; authenticating = credentialPending = false;
        var pending = cancellation; cancellation = null; pending?.Cancel(); pending?.Dispose();
        settingChange?.TrySetResult(reason); settingChange = null; desiredSetting = null;
    }

    public void Dispose()
    {
        disposed = true; CancelAuthentication("Authentication cancelled.");
        if (ReferenceEquals(MobileAppSecurity.Current, this)) MobileAppSecurity.Current = null;
    }

    private sealed class CancelButton(Action cancel) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => cancel();
    }

    private sealed class Authentication(BiometricAppLock owner, int attempt) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => owner.Finish(attempt, true);
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29) && errorCode is BiometricErrorCode.NoBiometrics or BiometricErrorCode.HwNotPresent or BiometricErrorCode.HwUnavailable)
                owner.UseCredential(attempt);
            else owner.Finish(attempt, false, errString?.ToString() ?? "Authentication failed.");
        }
        // Failed scans are nonterminal; Android keeps the prompt open for retry.
    }
}
