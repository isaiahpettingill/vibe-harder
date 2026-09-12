using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CodexManager;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsAndroid())
            Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<Avalonia.Controls.Primitives.ScrollBar>())
            {
                Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Primitives.ScrollBar.IsVisibleProperty, false) }
            });
    }
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = await Task.Run(() => { var store = new Store(backgroundWrites: true); return (Store: store, Workspaces: store.Workspaces(), Chats: store.Chats()); });
            desktop.MainWindow = new MainWindow(state.Store, state.Workspaces, state.Chats); desktop.MainWindow.Show();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView(new Store(), remoteOnly: true);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView(new Store(), remoteOnly: true);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
