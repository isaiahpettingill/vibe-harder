using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace CodexManager;

// One ordered writer owns its connection; UI reads use a different connection.
internal sealed class StoreWriter
{
    private sealed class WriteItem(Action<SqliteConnection> write, string? key = null, TaskCompletionSource? barrier = null)
    {
        public Action<SqliteConnection> Write = write;
        public readonly string? Key = key;
        public readonly TaskCompletionSource? Barrier = barrier;
    }
    private readonly Channel<WriteItem> queue = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, WriteItem> pending = [];
    private readonly object sync = new();
    private readonly Task worker;
    private bool closed;
    public StoreWriter(string connectionString)
    {
        worker = Task.Run(async () =>
        {
            var barriers = new List<TaskCompletionSource>();
            try
            {
                using var connection = new SqliteConnection(connectionString); connection.Open();
                using (var settings = connection.CreateCommand()) { settings.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; settings.ExecuteNonQuery(); }
                while (await queue.Reader.WaitToReadAsync())
                {
                    using var transaction = connection.BeginTransaction();
                    for (var count = 0; count < 128 && queue.Reader.TryRead(out var item); count++)
                    {
                        Action<SqliteConnection> write;
                        lock (sync)
                        {
                            write = item.Write;
                            if (item.Key is { } key && pending.GetValueOrDefault(key) == item) pending.Remove(key);
                        }
                        if (item.Barrier is not null) barriers.Add(item.Barrier);
                        write(connection);
                    }
                    transaction.Commit();
                    foreach (var barrier in barriers) barrier.TrySetResult();
                    barriers.Clear();
                }
            }
            catch (Exception error)
            {
                var failure = new IOException("Could not persist app data.", error);
                lock (sync) { closed = true; pending.Clear(); queue.Writer.TryComplete(failure); }
                foreach (var barrier in barriers) barrier.TrySetException(failure);
                while (queue.Reader.TryRead(out var item)) item.Barrier?.TrySetException(failure);
                throw failure;
            }
            finally { lock (sync) pending.Clear(); }
        });
    }
    public void Enqueue(Action<SqliteConnection> write, string? key = null)
    {
        lock (sync)
        {
            if (closed || worker.IsFaulted) throw new IOException("Could not persist app data: the writer has stopped.", worker.Exception);
            if (key is not null && pending.TryGetValue(key, out var existing)) { existing.Write = write; return; }
            var item = new WriteItem(write, key);
            if (!queue.Writer.TryWrite(item)) throw new IOException("The data writer is unavailable or its queue is full. Your storage may be too slow or unavailable.");
            if (key is null) pending.Clear(); else pending[key] = item;
        }
    }
    public async Task Flush()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WriteItem(static _ => { }, barrier: barrier);
        while (true)
        {
            lock (sync) { if (queue.Writer.TryWrite(item)) { pending.Clear(); break; } }
            if (!await queue.Writer.WaitToWriteAsync().ConfigureAwait(false)) { await worker.ConfigureAwait(false); return; }
        }
        var finished = await Task.WhenAny(barrier.Task, worker).ConfigureAwait(false); await finished.ConfigureAwait(false);
    }
    public async Task Close() { lock (sync) { closed = true; pending.Clear(); queue.Writer.TryComplete(); } await worker.ConfigureAwait(false); }
}
