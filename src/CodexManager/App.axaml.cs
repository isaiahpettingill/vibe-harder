using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CodexManager;

public partial class App : Application
{
    public static Action? DesktopReady { get; set; }
    public override void Initialize()
    {
        AppDiagnostics.Install();
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsAndroid())
            Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<Avalonia.Controls.Primitives.ScrollBar>())
            {
                Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Primitives.ScrollBar.IsVisibleProperty, false) }
            });
    }
    public override async void OnFrameworkInitializationCompleted()
    {
#if !MOBILE_CLIENT
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (this.TryGetFeature<IActivatableLifetime>() is { } activation)
            {
                void Reopen(object? sender, ActivatedEventArgs args)
                {
                    if (args.Kind != ActivationKind.Reopen) return;
                    if (desktop.MainWindow is MainWindow main) main.ShowFromTray();
                    else if (desktop.MainWindow is { } recovery) { recovery.Show(); recovery.Activate(); }
                }
                activation.Activated += Reopen;
                desktop.Exit += (_, _) => activation.Activated -= Reopen;
            }
            await OpenDesktop(desktop);
        }
        else
#endif
            if (ApplicationLifetime is IActivityApplicationLifetime activity)
            {
                activity.MainViewFactory = () => new MainView(new Store(), remoteOnly: true);
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                single.MainView = new MainView(new Store(), remoteOnly: true);
            }
        base.OnFrameworkInitializationCompleted();
    }

#if !MOBILE_CLIENT
    private static async Task OpenDesktop(IClassicDesktopStyleApplicationLifetime desktop, Avalonia.Controls.Window? recovery = null)
    {
        Store? openedStore = null;
        try
        {
            var state = await Task.Run(() => { openedStore = new Store(backgroundWrites: true); return (Store: openedStore, Workspaces: openedStore.Workspaces(), Chats: openedStore.Chats()); });
            var window = new MainWindow(state.Store, state.Workspaces, state.Chats);
            desktop.MainWindow = window; window.Show(); recovery?.Close();
            DesktopReady?.Invoke();
        }
        catch (Exception error) when (!AppDiagnostics.IsUnrecoverable(error))
        {
            try { openedStore?.Dispose(); } catch (Exception cleanup) { AppDiagnostics.Record("Startup cleanup", cleanup); }
            AppDiagnostics.Record("Restore application", error);
            var window = recovery ?? new Avalonia.Controls.Window { Title = "Restore Vibe Harder", Width = 520, Height = 280 };
            var retry = new Avalonia.Controls.Button { Content = "Retry" };
            retry.Click += async (_, _) => { retry.IsEnabled = false; await OpenDesktop(desktop, window); retry.IsEnabled = true; };
            window.Content = new Avalonia.Controls.StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children = { new Avalonia.Controls.TextBlock { Text = "Could not restore the app. Your saved chats have been kept.\n\n" + error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, retry }
            };
            desktop.MainWindow = window; window.Show();
        }
    }
#endif
}
