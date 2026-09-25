using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CodexManager;

public partial class MainView
{
    private readonly Dictionary<Chat, IDisposable> historyEvictions = [];
    private IDisposable? pendingSleep;
    private RenderTargetBitmap? sleepingFrame;
    private bool uiSleeping;
    private int? sleepingPageEnd;
    private int sleepingPageCount;
    private (object Item, double Within)? sleepingAnchor;
    public bool IsPresentationSleeping => uiSleeping;
    private void InitializePresentationSleep()
    {
        AddHandler(PointerPressedEvent, (_, _) => WakePresentation(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => WakePresentation(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    private void WakePresentation()
    {
        pendingSleep?.Dispose(); pendingSleep = null;
        _ = SetPresentationSleeping(false);
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
        if (store.Setting("presentationSleep") != "1" || desktopWindow is { IsVisible: true, IsActive: true } && desktopWindow.WindowState != WindowState.Minimized)
        { _ = SetPresentationSleeping(false); return; }
        pendingSleep = DispatcherTimer.RunOnce(async () =>
        {
            pendingSleep = null;
            if (store.Setting("presentationSleep") == "1" && (desktopWindow is not { IsVisible: true, IsActive: true } || desktopWindow.WindowState == WindowState.Minimized))
                await SetPresentationSleeping(true);
        }, TimeSpan.FromSeconds(2));
    }
    public async Task SetPresentationSleeping(bool sleeping)
    {
        if (closing || sleeping == uiSleeping) return;
        uiSleeping = sleeping;
        System.Diagnostics.Trace.WriteLine(sleeping ? "Presentation sleeping" : "Presentation awake");
        if (sleeping)
        {
            await SaveAllAsync();
            if (!uiSleeping || closing) return;
            pageLoad?.Cancel();
            sleepingPageEnd = viewingHistory ? MessageList.Items.OfType<Message>().LastOrDefault()?.Sequence : null;
            sleepingPageCount = MessageList.ItemCount;
            sleepingAnchor = (MessageList.ItemsPanelRoot as TranscriptPanel)?.CaptureAnchor();
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
            if (sleepingPageEnd is null) MessageList.ItemsSource = selected.Messages;
            try
            {
                if (sleepingPageEnd is { } end)
                {
                    var page = await store.ReadPageAsync(selected, before: end + 1, limit: sleepingPageCount, token: discoveryLifetime.Token);
                    if (!uiSleeping && ReferenceEquals(current, selected))
                    {
                        MessageList.ItemsSource = page;
                        MessageList.UpdateLayout();
                        (MessageList.ItemsPanelRoot as TranscriptPanel)?.RestoreAnchor(sleepingAnchor);
                        MessageList.UpdateLayout();
                    }
                }
                else await RestoreVisibleHistory(selected, discoveryLifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { StatusText.Text = "Could not reload history: " + error.Message; }
        }
        sleepingAnchor = null;
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
        var merged = HistoryWindow.Bound(page.Concat(chat.Messages), newer: true);
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
