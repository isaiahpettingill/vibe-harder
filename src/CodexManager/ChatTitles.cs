namespace CodexManager;

// A chat starts with its first line as title. Agents that name sessions replace it, except
// after the user picked a title themselves.
public static class ChatTitles
{
    private static string Key(Chat chat) => "renamed:" + chat.Id;
    public static bool Renamed(Store store, Chat chat) => store.Setting(Key(chat)) == "1";
    public static void MarkRenamed(Store store, Chat chat) => store.Setting(Key(chat), "1");
}
