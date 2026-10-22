using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
    private readonly DispatcherTimer healthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? nextEyeBreak;
    private DateTimeOffset? nextWalkBreak;
    private DateTimeOffset? breakStarted;
    private DateTimeOffset? lastBreak;
    private bool walkingBreak;
    private string? illustrationAccent;

    private void InitializeHealthReminders()
    {
        if (remoteOnly) return;
        if (DateTimeOffset.TryParse(store.Setting("lastHealthBreak"), out var savedBreak)) lastBreak = savedBreak;
        healthTimer.Tick += (_, _) => TickHealthReminders();
        AppTheme.Changed += UpdateBreakIllustration;
        ConfigureHealthReminders();
        healthTimer.Start();
    }

    private void ConfigureHealthReminders()
    {
        var now = DateTimeOffset.Now;
        nextEyeBreak = store.Setting("eyeHealthMode") == "1" ? nextEyeBreak ?? now.AddMinutes(20) : null;
        nextWalkBreak = store.Setting("walkingHealthMode") == "1" ? nextWalkBreak ?? now.AddMinutes(30) : null;
        if (nextEyeBreak is null && nextWalkBreak is null) HealthOverlay.IsVisible = false;
        UpdateHealthStatus();
    }

    private void TickHealthReminders()
    {
        if (closing) return;
        var now = DateTimeOffset.Now;
        var eyeDue = nextEyeBreak <= now;
        var walkDue = nextWalkBreak <= now;
        if (eyeDue) { do { nextEyeBreak = nextEyeBreak!.Value.AddMinutes(20); } while (nextEyeBreak <= now); }
        if (walkDue) { do { nextWalkBreak = nextWalkBreak!.Value.AddMinutes(30); } while (nextWalkBreak <= now); }
        if (walkDue || (eyeDue && !HealthOverlay.IsVisible)) ShowHealthBreak(walkDue);
        UpdateHealthStatus();
    }

    private void ShowHealthBreak(bool walk)
    {
        if (!HealthOverlay.IsVisible) breakStarted = DateTimeOffset.Now;
        walkingBreak = walk;
        BreakHeading.Text = walk ? "take a quick walk" : "Look at something far away";
        HealthOverlay.IsVisible = true;
        UpdateBreakIllustration();
    }

    private void UpdateBreakIllustration()
    {
        if (!HealthOverlay.IsVisible || AppTheme.Current is null) return;
        var accent = AppTheme.Current.Accent.TrimStart('#').ToUpperInvariant();
        var illustration = walkingBreak ? $"Walk-{accent}" : accent;
        if (illustrationAccent == illustration) return;
        var uri = new Uri($"avares://VibeHarder.UI/Assets/Breaks/{illustration}.png");
        BreakIllustration.Source = new Bitmap(AssetLoader.Open(uri));
        illustrationAccent = illustration;
    }

    private void UpdateHealthStatus()
    {
        var now = DateTimeOffset.Now;
        ClockText.IsVisible = store.Setting("showClock") != "0";
        ClockText.Text = now.ToString("t");
        BreakStatus.IsVisible = nextEyeBreak is not null || nextWalkBreak is not null;
        if (BreakStatus.IsVisible)
        {
            var elapsed = now - (lastBreak ?? now);
            BreakStatus.Text = lastBreak is null ? "No break yet" : $"Last break {Math.Max(0, (int)elapsed.TotalMinutes)}m ago";
        }
        if (HealthOverlay.IsVisible && breakStarted is { } started)
        {
            var elapsed = now - started;
            var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
            BreakTimerText.Text = $"{seconds / 60}:{seconds % 60:00}" + (elapsed >= (walkingBreak ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20)) ? "  ✓" : "");
        }
    }

    private void BreakDoneClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        lastBreak = DateTimeOffset.Now;
        store.Setting("lastHealthBreak", lastBreak.Value.ToString("O"));
        breakStarted = null;
        HealthOverlay.IsVisible = false;
        UpdateHealthStatus();
    }
}
