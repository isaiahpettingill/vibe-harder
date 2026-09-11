using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainWindow
{
    private readonly Dictionary<Chat, IDisposable> historyEvictions = [];
    private IDisposable? pendingSleep;
    private RenderTargetBitmap? sleepingFrame;
    private bool uiSleeping;
    public bool IsPresentationSleeping => uiSleeping;
    private void InitializePresentationSleep()
    {
        Activated += (_, _) => SchedulePresentationSleep();
        Deactivated += (_, _) => SchedulePresentationSleep();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) SchedulePresentationSleep(); };
    }
    private void KeepHistory(Chat chat)
    {
        if (historyEvictions.Remove(chat, out var pending)) pending.Dispose();
        chat.RetainHistory = true;
    }
    private void DeferHistoryEviction(Chat chat)
    {
        if (historyEvictions.Remove(chat, out var pending)) pending.Dispose();
        historyEvictions[chat] = DispatcherTimer.RunOnce(() =>
        {
            historyEvictions.Remove(chat);
            if (closing || ReferenceEquals(current, chat) && remoteView is null && !uiSleeping) return;
            chat.RetainHistory = false;
            store.TrimHistory(chat); store.ReleaseHistory(chat);
        }, TimeSpan.FromSeconds(2));
    }
    private void SchedulePresentationSleep()
    {
        if (closing) return;
        pendingSleep?.Dispose(); pendingSleep = null;
        // Headless hosts have no desktop visibility and continue processing RPC.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) return;
        if (store.Setting("presentationSleep") == "0" || IsVisible && IsActive && WindowState != WindowState.Minimized)
        { _ = SetPresentationSleeping(false); return; }
        pendingSleep = DispatcherTimer.RunOnce(async () => await SetPresentationSleeping(true), TimeSpan.FromSeconds(2));
    }
    public async Task SetPresentationSleeping(bool sleeping)
    {
        if (closing || sleeping == uiSleeping) return;
        uiSleeping = sleeping;
        if (sleeping)
        {
            SaveAll(); pageLoad?.Cancel();
            if (IsVisible && RootPanes.Bounds.Width > 0 && RootPanes.Bounds.Height > 0)
            {
                try
                {
                    sleepingFrame = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(RootPanes.Bounds.Width), (int)Math.Ceiling(RootPanes.Bounds.Height)));
                    sleepingFrame.Render(RootPanes);
                }
                catch { sleepingFrame?.Dispose(); sleepingFrame = null; }
            }
            // Detach the tree to stop control animation timers; display one still frame.
            Content = new Image { Source = sleepingFrame, Stretch = Stretch.Fill };
            MessageList.ItemsSource = null; AttachmentList.ItemsSource = null;
            remoteView?.SetPresentationSleeping(true);
            foreach (var chat in chats)
            { chat.RetainHistory = false; store.TrimHistory(chat); store.ReleaseHistory(chat); }
            return;
        }
        Content = RootPanes; sleepingFrame?.Dispose(); sleepingFrame = null;
        remoteView?.SetPresentationSleeping(false);
        if (remoteView is null && current is { } selected)
        {
            KeepHistory(selected); AttachmentList.ItemsSource = selected.Attachments;
            MessageList.ItemsSource = selected.Messages;
            try { await RestoreVisibleHistory(selected, discoveryLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { StatusText.Text = "Could not reload history: " + error.Message; }
        }
        if (!closing) UpdateControls();
    }
    private async Task RestoreVisibleHistory(Chat chat, CancellationToken token)
    {
        if (runtimes.GetValueOrDefault(chat.Id)?.IsLoadingHistory == true) return;
        foreach (var message in chat.Messages) store.SaveMessage(chat, message);
        var page = await store.ReadPageAsync(chat, token: token);
        if (closing || uiSleeping || remoteView is not null || !ReferenceEquals(current, chat)) return;
        // Streaming may advance while disk IO is pending. Preserve the live tail.
        var nextSequence = chat.NextSequence;
        var merged = page.Concat(chat.Messages).GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).TakeLast(Chat.HistoryPageSize).ToArray();
        store.ApplyRecentPage(chat, merged); chat.NextSequence = Math.Max(nextSequence, chat.NextSequence);
        ScrollTranscriptToEnd();
    }
    private void DisposePresentationSleep()
    {
        pendingSleep?.Dispose();
        foreach (var pending in historyEvictions.Values) pending.Dispose();
        historyEvictions.Clear(); sleepingFrame?.Dispose(); sleepingFrame = null;
    }
}
