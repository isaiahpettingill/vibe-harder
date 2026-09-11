using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CodexManager;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = await Task.Run(() => { var store = new Store(backgroundWrites: true); return (Store: store, Workspaces: store.Workspaces(), Chats: store.Chats()); });
            desktop.MainWindow = new MainWindow(state.Store, state.Workspaces, state.Chats); desktop.MainWindow.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
