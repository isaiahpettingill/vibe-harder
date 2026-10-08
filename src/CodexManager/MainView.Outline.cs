namespace CodexManager;

public partial class MainView
{
    // The whole chat's outline sizes the transcript's scrollbar; refreshed as history pages load.
    private async void RefreshOutline(Chat chat)
    {
        if (TranscriptPanel.GetOutline(MessageList)?.ChatKey != chat.Id) TranscriptPanel.SetOutline(MessageList, null);
        try
        {
            var outline = RemoteSessionOf(chat) is { } remote ? await remote.ReadOutline() : await store.ReadOutlineAsync(chat, discoveryLifetime.Token);
            if (ReferenceEquals(current, chat) && !closing) TranscriptPanel.SetOutline(MessageList, outline);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Transcript outline", error); }
    }
}
