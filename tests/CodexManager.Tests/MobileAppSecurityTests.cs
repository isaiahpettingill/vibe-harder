using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class MobileAppSecurityTests
{
    [AvaloniaFact]
    public void FilePickerExemptsOnlyOnePauseAndAlwaysCleansUp()
    {
        using (MobileAppSecurity.BeginFilePicker())
        {
            Assert.True(MobileAppSecurity.ConsumeFilePickerPause());
            Assert.False(MobileAppSecurity.ConsumeFilePickerPause());
        }
        Assert.False(MobileAppSecurity.ConsumeFilePickerPause());
        using (MobileAppSecurity.BeginFilePicker()) { }
        Assert.False(MobileAppSecurity.ConsumeFilePickerPause());
    }

    private sealed class Security : IMobileAppSecurity
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource<string> Result { get; } = new();
        public bool? Requested { get; private set; }
        public Task<string> ChangeEnabled(bool enabled) { Requested = enabled; return Result.Task; }
    }

    [AvaloniaFact]
    public async Task SettingWaitsForAuthenticationAndPreservesStateOnCancellation()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("app-security-").FullName);
        var security = new Security { Enabled = true }; MobileAppSecurity.Current = security;
        try
        {
            using var settings = new ConnectionSettingsView(store, true, () => Task.CompletedTask);
            var button = settings.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "BiometricUnlock");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(button.IsEnabled); Assert.False(security.Requested);
            Assert.True(security.Enabled);
            security.Result.SetResult("Authentication cancelled.");
            await Task.Yield();
            Assert.True(button.IsEnabled);
            Assert.Equal("Turn off biometric unlock", button.Content);
            Assert.Equal("Authentication cancelled.", settings.GetLogicalDescendants().OfType<TextBlock>().Single(b => b.Name == "BiometricUnlockStatus").Text);
        }
        finally { MobileAppSecurity.Current = null; }
    }

    [AvaloniaFact]
    public void DesktopDoesNotOfferAndroidAppLock()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("app-security-desktop-").FullName);
        using var settings = new ConnectionSettingsView(store, false, () => Task.CompletedTask);
        Assert.DoesNotContain(settings.GetLogicalDescendants().OfType<Button>(), b => b.Name == "BiometricUnlock");
    }
}
