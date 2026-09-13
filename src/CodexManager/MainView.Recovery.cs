using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private Border? recoveryNotice;
    private bool recoveringPresentation;
    private int recoveryAttempts;
    private DateTimeOffset previousRecovery;

    private void ClearRecoveryNotice()
    {
        if (recoveryNotice is not null) RootPanes.Children.Remove(recoveryNotice);
        recoveryNotice = null;
        if (remoteView is not null) remoteView.IsVisible = true;
    }

    public async Task RecoverAfterError(Exception error)
    {
        if (closing || recoveringPresentation) return;
        recoveringPresentation = true;
        var now = DateTimeOffset.UtcNow;
        recoveryAttempts = now - previousRecovery < TimeSpan.FromSeconds(15) ? recoveryAttempts + 1 : 1;
        previousRecovery = now;
        try
        {
            ClearRecoveryNotice();
            var text = new TextBlock { Text = "Restoring the chat view… Your agents are still running.", TextWrapping = TextWrapping.Wrap };
            var retry = new Button { Content = "Reload chat", IsVisible = false };
            retry.Click += async (_, _) => { recoveryAttempts = 0; previousRecovery = default; await RecoverAfterError(error); };
            recoveryNotice = new Border { Name = "ChatRecoveryNotice", Padding = new Thickness(16), ZIndex = 200, Child = new StackPanel { Spacing = 10, Children = { text, retry } } };
            recoveryNotice.Bind(BackgroundProperty, this.GetResourceObservable("AppSurface"));
            Grid.SetRow(recoveryNotice, 1); Grid.SetColumn(recoveryNotice, 2); RootPanes.Children.Add(recoveryNotice);
            if (recoveryAttempts > 2)
            {
                MessageList.ItemsSource = null;
                if (remoteView is not null) { remoteView.SetPresentationSleeping(true); remoteView.IsVisible = false; }
                text.Text = "This chat view could not recover automatically. Your agents are still running. Reload it or select another chat.\n\n" + error.Message;
                retry.IsVisible = true; return;
            }
            try
            {
                // Rebuild presentation only; do not dispose live runtimes or resend requests.
                if (remoteView is not null) remoteView.RestorePresentation();
                else if (current is { } chat)
                {
                    var draft = Composer.Text;
                    MessageList.ItemsSource = null;
                    if (!chat.Busy)
                    {
                        foreach (var message in chat.Messages) store.SaveMessage(chat, message);
                        await store.FlushAsync();
                        var page = await store.ReadPageAsync(chat);
                        if (closing || !ReferenceEquals(current, chat)) return;
                        store.ApplyRecentPage(chat, page);
                    }
                    MessageList.ItemsSource = chat.Messages; Composer.Text = draft;
                }
                BuildWorkspaceTree(); UpdateControls();
                await Task.Delay(250);
                ClearRecoveryNotice();
            }
            catch (Exception failure) when (!AppDiagnostics.IsUnrecoverable(failure))
            {
                AppDiagnostics.Record("Chat view recovery", failure);
                MessageList.ItemsSource = null;
                if (remoteView is not null) { remoteView.SetPresentationSleeping(true); remoteView.IsVisible = false; }
                text.Text = "Could not restore this chat view. Your agents are still running. You can reload it or select another chat.\n\n" + failure.Message;
                retry.IsVisible = true;
            }
        }
        finally { recoveringPresentation = false; }
    }
}
