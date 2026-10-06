namespace CodexManager;

// Tells the user when a chat finishes, or when an agent comes back from background work, while
// they are looking elsewhere. Desktop notifications go through the system notifier; paired phones
// get a UnifiedPush message from the host.
public static class ChatNotifications
{
    public const string EnabledKey = "completionNotifications";
    public static bool Enabled(Store store) => store.Setting(EnabledKey) != "0";
    // Paired phones are notified by the computer that runs the chat.
    public static Func<Chat, string, Task>? Push { get; set; }

    public static void Completed(Store store, Chat chat, bool background, Action open, bool desktop = true)
    {
        if (!Enabled(store)) return;
        var title = background ? "Back from background work" : "Reply ready";
        if (desktop) PermissionNotifications.Show("done:" + chat.Id, chat.Title, open, title, ChatLinks.For(chat.Id));
        if (Push is { } push && !chat.IsRemote) _ = SendPush(push, chat, title);
    }
    private static async Task SendPush(Func<Chat, string, Task> push, Chat chat, string title)
    {
        try { await push(chat, title); }
        catch (Exception error) { AppDiagnostics.Record("Push notification", error); }
    }
}
