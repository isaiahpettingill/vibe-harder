using Avalonia.Threading;

namespace CodexManager;

public sealed class RemoteDownloads
{
    public sealed class Transfer(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public long Received { get; internal set; }
        public long? Total { get; internal set; }
    }

    private readonly List<Transfer> active = [];
    private int nextId;
    private DateTime lastNotification;
    public IReadOnlyList<Transfer> Active => active;
    public event Action? Changed;

    public Transfer Begin(string target)
    {
        var name = target.TrimEnd('/', '\\').Replace('\\', '/').Split('/').LastOrDefault();
        var transfer = new Transfer(++nextId, string.IsNullOrWhiteSpace(name) ? target : name);
        active.Add(transfer); Changed?.Invoke();
        return transfer;
    }

    public void Report(int id, long received, long? total)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Report(id, received, total)); return; }
        var transfer = active.FirstOrDefault(t => t.Id == id);
        if (transfer is null) return;
        transfer.Received = received; transfer.Total = total;
        if (DateTime.UtcNow - lastNotification < TimeSpan.FromMilliseconds(100) && received != total) return;
        lastNotification = DateTime.UtcNow; Changed?.Invoke();
    }

    public void Finish(int id)
    {
        var transfer = active.FirstOrDefault(t => t.Id == id);
        if (transfer is null) return;
        active.Remove(transfer); Changed?.Invoke();
    }
}
