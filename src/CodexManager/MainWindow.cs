using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CodexManager;

// Desktop chrome only. Android hosts the same MainView directly.
public sealed class MainWindow : Window
{
    public MainView View { get; }
    public MainWindow() : this(new Store()) { }
    public MainWindow(Store store, List<Workspace>? workspaces = null, List<Chat>? chats = null)
    {
        Title = "Vibe Harder"; Width = 1220; Height = 840; MinWidth = 360; MinHeight = 480;
        using var icon = AssetLoader.Open(new Uri("avares://VibeHarder.UI/Assets/app.ico"));
        Icon = new WindowIcon(icon);
        View = new MainView(store, workspaces, chats);
        Content = View;
        NameScope.SetNameScope(this, NameScope.GetNameScope(View));
        View.AttachDesktop(this);
    }
    public void ShowFromTray() => View.ShowFromTray();
    public void RequestExit() => View.RequestExit();
    public bool IsPresentationSleeping => View.IsPresentationSleeping;
    public Task SetPresentationSleeping(bool sleeping) => View.SetPresentationSleeping(sleeping);
}
