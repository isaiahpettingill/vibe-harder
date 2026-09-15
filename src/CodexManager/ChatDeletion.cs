namespace CodexManager;

public static class ChatDeletion
{
    public static async Task<string?> Delete(Store store, IList<Chat> chats, Chat chat, Workspace workspace, Func<Task> release, Func<Task<string?>>? providerDelete = null)
    {
        if (chat.Busy || chat.IsDeleting) throw new IOException("Stop the chat before deleting it.");
        if (!chats.Contains(chat)) throw new IOException("This chat has already been removed.");
        chat.IsDeleting = true; chat.Busy = true; chat.Status = "Deleting…";
        try
        {
            await release();
            chat.Busy = true;
            string? warning;
            try { warning = await (providerDelete?.Invoke() ?? ChatHistory.TryDeleteFromProvider(store, workspace, chat)); }
            catch (Exception error) { warning = error.Message; }
            if (chat.SessionId is not null) store.Setting(AgentProviders.HiddenHistoryKey(chat), "1");
            store.Delete(chat); chats.Remove(chat);
            return warning;
        }
        finally { chat.IsDeleting = false; chat.Busy = false; }
    }
}
